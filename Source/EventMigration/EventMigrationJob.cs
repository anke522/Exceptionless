﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless.Core.Caching;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Geo;
using Exceptionless.Core.Jobs;
using Exceptionless.Core.Lock;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Plugins.EventUpgrader;
using Exceptionless.Core.Queues;
using Exceptionless.Core.Utility;
using Exceptionless.Extensions;
using Exceptionless.Models;
using Exceptionless.Models.Data;
using FluentValidation;
using MongoDB.Bson;
using MongoDB.Driver.Builders;
using Nest;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog.Fluent;
using OldModels = Exceptionless.EventMigration.Models;

namespace Exceptionless.EventMigration {
    public class EventMigrationJob : MigrationJobBase {
        private readonly IQueue<EventMigrationBatch> _queue;

        public EventMigrationJob(IQueue<EventMigrationBatch> queue, IElasticClient elasticClient, EventUpgraderPluginManager eventUpgraderPluginManager, IValidator<Stack> stackValidator, IValidator<PersistentEvent> eventValidator, IGeoIPResolver geoIpResolver, ILockProvider lockProvider, ICacheClient cache)
            : base(elasticClient, eventUpgraderPluginManager, stackValidator, eventValidator, geoIpResolver, lockProvider, cache) {
            _queue = queue;
        }

        protected override async Task<JobResult> RunInternalAsync(CancellationToken token) {
            OutputPublicIp();
            QueueEntry<EventMigrationBatch> queueEntry = null;
            try {
                queueEntry = _queue.Dequeue(TimeSpan.FromSeconds(1));
            } catch (Exception ex) {
                if (!(ex is TimeoutException)) {
                    Log.Error().Exception(ex).Message("Error trying to dequeue message: {0}", ex.Message).Write();
                    return JobResult.FromException(ex);
                }
            }

            if (queueEntry == null)
                return JobResult.Success;

            Log.Info().Message("Processing event migration jobs for date range: {0}-{1}", new DateTimeOffset(queueEntry.Value.StartTicks, TimeSpan.Zero).ToString("O"), new DateTimeOffset(queueEntry.Value.EndTicks, TimeSpan.Zero).ToString("O")).Write();
       
            int total = 0;
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var errorCollection = GetErrorCollection();
            var knownStackIds = new List<string>();

            var serializerSettings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Ignore };
            serializerSettings.AddModelConverters();

            var query = Query.And(Query.GTE(ErrorFieldNames.OccurrenceDate_UTC, queueEntry.Value.StartTicks), Query.LT(ErrorFieldNames.OccurrenceDate_UTC, queueEntry.Value.EndTicks));
            var errors = errorCollection.Find(query).SetSortOrder(SortBy.Ascending(ErrorFieldNames.OccurrenceDate_UTC)).SetLimit(_batchSize).ToList();
            while (errors.Count > 0) {
                Log.Info().Message("Migrating events {0}-{1} {2:N0} total {3:N0}/s...", errors.First().Id, errors.Last().Id, total, total > 0 ? total / stopwatch.Elapsed.TotalSeconds : 0).Write();

                var upgradedErrors = JArray.FromObject(errors);
                var ctx = new EventUpgraderContext(upgradedErrors, new Version(1, 5), true);
                _eventUpgraderPluginManager.Upgrade(ctx);

                var upgradedEvents = upgradedErrors.FromJson<PersistentEvent>(serializerSettings);

                var stackIdsToCheck = upgradedEvents.Where(e => !knownStackIds.Contains(e.StackId)).Select(e => e.StackId).Distinct().ToArray();
                if (stackIdsToCheck.Length > 0)
                    knownStackIds.AddRange(_eventRepository.ExistsByStackIds(stackIdsToCheck));
                        
                upgradedEvents.ForEach(e => {
                    if (e.Date.UtcDateTime > DateTimeOffset.UtcNow.AddHours(1))
                        e.Date = DateTimeOffset.Now;

                    e.CreatedUtc = e.Date.ToUniversalTime().DateTime;

                    // Truncate really large fields
                    if (e.Message != null && e.Message.Length > 2000) {
                        Log.Error().Project(e.ProjectId).Message("Event: {0} Message is Too Big: {1}", e.Id, e.Message.Length).Write();
                        e.Message = e.Message.Truncate(2000);
                    }

                    if (e.Source != null && e.Source.Length > 2000) {
                        Log.Error().Project(e.ProjectId).Message("Event: {0} Source is Too Big: {1}", e.Id, e.Source.Length).Write();
                        e.Source = e.Source.Truncate(2000);
                    }

                    if (!knownStackIds.Contains(e.StackId)) {
                        // We haven't processed this stack id yet in this run. Check to see if this stack has already been imported..
                        e.IsFirstOccurrence = true;
                        knownStackIds.Add(e.StackId);
                    }

                    var request = e.GetRequestInfo();   
                    if (request != null)
                        e.AddRequestInfo(request.ApplyDataExclusions(RequestInfoPlugin.DefaultExclusions, RequestInfoPlugin.MAX_VALUE_LENGTH));

                    foreach (var ip in GetIpAddresses(e, request)) {
                        var location = _geoIpResolver.ResolveIp(ip);
                        if (location == null || !location.IsValid())
                            continue;

                        e.Geo = location.ToString();
                        break;
                    }

                    if (e.Type == Event.KnownTypes.NotFound && request != null) {
                        if (String.IsNullOrWhiteSpace(e.Source)) {
                            e.Message = null;
                            e.Source = request.GetFullPath(includeHttpMethod: true, includeHost: false, includeQueryString: false);
                        }

                        return;
                    }
                         
                    var error = e.GetError();
                    if (error == null) {
                        Debugger.Break();
                        Log.Error().Project(e.ProjectId).Message("Unable to get parse error model: {0}", e.Id).Write();
                        return;
                    }

                    var stackingTarget = error.GetStackingTarget();
                    if (stackingTarget != null && stackingTarget.Method != null && !String.IsNullOrEmpty(stackingTarget.Method.GetDeclaringTypeFullName()))
                        e.Source = stackingTarget.Method.GetDeclaringTypeFullName().Truncate(2000);

                    var signature = new ErrorSignature(error);
                    if (signature.SignatureInfo.Count <= 0)
                        return;

                    var targetInfo = new SettingsDictionary(signature.SignatureInfo);
                    if (stackingTarget != null && stackingTarget.Error != null && !targetInfo.ContainsKey("Message"))
                        targetInfo["Message"] = error.GetStackingTarget().Error.Message;

                    error.Data[Error.KnownDataKeys.TargetInfo] = targetInfo;
                });

                Log.Info().Message("Saving events {0}-{1} {2:N0} total", errors.First().Id, errors.Last().Id, upgradedEvents.Count).Write();
                try {
                    _eventRepository.Add(upgradedEvents, sendNotification: false);
                } catch (Exception) {
                    foreach (var persistentEvent in upgradedEvents) {
                        try {
                            _eventRepository.Add(persistentEvent, sendNotification: false);
                        } catch (Exception ex) {
                            //Debugger.Break();
                            Log.Error().Exception(ex).Project(persistentEvent.ProjectId).Message("An error occurred while migrating event '{0}': {1}", persistentEvent.Id, ex.Message).Write();
                        }
                    }
                }

                total += upgradedEvents.Count;
                var lastId = upgradedEvents.Last().Id;
                _cache.Set("migration-errorid", lastId);

                Log.Info().Message("Getting next batch of events").Write();
                errors = errorCollection.Find(Query.And(Query.GT(ErrorFieldNames.Id, ObjectId.Parse(lastId)), Query.LT(ErrorFieldNames.OccurrenceDate_UTC, queueEntry.Value.EndTicks)))
                                        .SetSortOrder(SortBy.Ascending(ErrorFieldNames.OccurrenceDate_UTC))
                                        .SetLimit(_batchSize).ToList();
            }


            Log.Info().Message("Finished processing event migration jobs for date range: {0}-{1}", new DateTimeOffset(queueEntry.Value.StartTicks, TimeSpan.Zero).ToString("O"), new DateTimeOffset(queueEntry.Value.EndTicks, TimeSpan.Zero).ToString("O")).Write();
            _cache.Set("migration-completedday", queueEntry.Value.EndTicks);
            queueEntry.Complete();

            return JobResult.Success;
        }
    }

    public class EventMigrationBatch {
        public long StartTicks { get; set; }
        public long EndTicks { get; set; }
    }
}
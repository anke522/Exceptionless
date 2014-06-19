﻿#region Copyright 2014 Exceptionless

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// 
//     http://www.apache.org/licenses/LICENSE-2.0

#endregion

using System;
using System.Collections.Generic;
using Exceptionless.Models;

namespace Exceptionless.Submission {
    public interface ISubmissionClient {
        SubmissionResponse Submit(IEnumerable<Event> events, ExceptionlessConfiguration config, IJsonSerializer serializer);
        SettingsResponse GetSettings(ExceptionlessConfiguration configuration, IJsonSerializer serializer);
    }
}
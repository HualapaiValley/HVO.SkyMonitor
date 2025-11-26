using System;
using System.Collections.Generic;

namespace HVO.SkyMonitor.AgentCore;

public sealed record FrameMetadata(
    TimeSpan Exposure,
    double Gain,
    double TemperatureC,
    string? SourceId = null,
    IReadOnlyDictionary<string, string>? Extra = null);

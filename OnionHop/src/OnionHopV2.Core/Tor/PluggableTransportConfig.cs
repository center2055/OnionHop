using System.Collections.Generic;

namespace OnionHopV2.Core.Tor;

internal sealed class PluggableTransportConfig
{
    public string? RecommendedDefault { get; set; }
    public Dictionary<string, string> PluggableTransports { get; set; } = new();
    public Dictionary<string, List<string>> Bridges { get; set; } = new();
}


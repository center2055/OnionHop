using System;
using System.Collections.Generic;

namespace OnionHopV3.Core.Models;

public sealed class TorRelayInfo
{
    public string Nickname { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public long AdvertisedBandwidth { get; set; }
    public DateTimeOffset? FirstSeenUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastRestartedUtc { get; set; }
}

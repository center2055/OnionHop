using System;

namespace OnionHopV3.Core.Models;

/// <summary>
/// One entry in the saved-bridges library (v3.6). Holds a bridge line the user chose to keep - from a
/// bridge scan, an SNI scan, or a manual add - so it can be reused later without re-scanning. Kept
/// deliberately small and JSON-friendly (see <see cref="OnionHopV3.Core.Services.SavedBridgeStore"/>).
/// </summary>
public sealed class SavedBridge
{
    /// <summary>Stable id derived from the normalized line, used for dedupe and delete.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The raw bridge line (for <see cref="SavedBridgeKind.Bridge"/>) or the SNI/front host
    /// (for <see cref="SavedBridgeKind.Sni"/>).</summary>
    public string Line { get; set; } = string.Empty;

    /// <summary>"bridge" or "sni" - what this entry represents.</summary>
    public string Kind { get; set; } = SavedBridgeKind.Bridge;

    /// <summary>Transport for a bridge (obfs4/webtunnel/…), or empty for an SNI host.</summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>Optional user-supplied label/name.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Where it came from: "bridge-scan", "sni-scan", "manual", etc.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>When it was saved (UTC, ISO 8601).</summary>
    public string AddedUtc { get; set; } = string.Empty;

    /// <summary>Last observed reachability status ("reachable"/"slow"/"unreachable"/"") at save time.</summary>
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>Last observed latency in ms, when known.</summary>
    public int? LastPingMs { get; set; }
}

/// <summary>The kinds of entry the saved library holds.</summary>
public static class SavedBridgeKind
{
    public const string Bridge = "bridge";
    public const string Sni = "sni";
}

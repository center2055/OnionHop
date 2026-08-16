using System.Text.Json.Serialization;

namespace OnionHopV3.Core.Models;

/// <summary>
/// One onion service the user publishes: something listening on their own network gets a `.onion`
/// address others can reach, without port forwarding, a static IP or DynDNS, and without exposing the
/// device to the open internet where scanners find it (#77).
///
/// The address is derived from <see cref="PrivateKey"/>, so that key IS the identity of the service:
/// lose it and the address changes, leak it and someone else can impersonate the address. It is stored
/// through <see cref="ProtectedStringJsonConverter"/> (DPAPI-encrypted on Windows) for that reason.
/// </summary>
public sealed class OnionService
{
    /// <summary>Stable id used for edit and delete.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-supplied name, e.g. "Camera". Display only.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Port on the `.onion` address that callers connect to.</summary>
    public int OnionPort { get; set; } = 80;

    /// <summary>Host the traffic is forwarded to, e.g. 127.0.0.1 or 192.168.1.50.</summary>
    public string TargetHost { get; set; } = "127.0.0.1";

    /// <summary>Port on <see cref="TargetHost"/> the traffic is forwarded to.</summary>
    public int TargetPort { get; set; } = 80;

    /// <summary>
    /// The ED25519-V3 key as Tor returns it, including the "ED25519-V3:" prefix. Null until the
    /// service is published for the first time, after which it is what keeps the address stable.
    /// </summary>
    [JsonConverter(typeof(ProtectedStringJsonConverter))]
    public string? PrivateKey { get; set; }

    /// <summary>Last published address without the ".onion" suffix, cached so the UI can show it
    /// while disconnected.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Whether to publish this service on connect.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The full hostname to hand out, or empty before the first publish.</summary>
    [JsonIgnore]
    public string Hostname => string.IsNullOrWhiteSpace(Address) ? string.Empty : Address + ".onion";
}

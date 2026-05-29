namespace OnionHopV3.Core.Models;

public sealed class TorCountryNodeStats
{
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public int TotalNodes { get; set; }
    public int EntryNodes { get; set; }
    public int ExitNodes { get; set; }
}

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

/// <summary>
/// Saved-bridges library subtab (v3.6). Shows the persistent library (<see cref="SavedBridgeStore"/>)
/// and lets the user apply, relabel, delete or clear saved bridges and SNI hosts.
/// </summary>
public sealed partial class SavedBridgesViewModel : ObservableObject
{
    private readonly AppStateViewModel _state;
    private readonly SavedBridgeStore _store;

    public SavedBridgesViewModel(AppStateViewModel state, SavedBridgeStore store)
    {
        _state = state;
        _store = store;
        Refresh();
    }

    public ObservableCollection<SavedBridgeRow> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _count;

    [ObservableProperty] private string _statusText = string.Empty;

    public bool HasItems => Count > 0;

    public void Refresh()
    {
        Items.Clear();
        foreach (var entry in _store.Load().OrderByDescending(e => e.AddedUtc, StringComparer.Ordinal))
        {
            Items.Add(new SavedBridgeRow(entry));
        }

        Count = Items.Count;
    }

    /// <summary>Apply one saved entry: a bridge goes to the custom-bridge list (Custom source), an SNI
    /// host goes to the custom-SNI list.</summary>
    [RelayCommand]
    private void Apply(SavedBridgeRow? row)
    {
        if (row == null)
        {
            return;
        }

        if (row.IsSni)
        {
            _state.CustomSniHosts = AppendUnique(_state.CustomSniHosts, row.Line, ", ");
            _state.AppendLog($"Saved library: applied SNI host '{row.Line}' to your custom SNI hosts.");
            StatusText = $"Applied SNI host '{row.Line}'.";
        }
        else
        {
            _state.CustomBridges = AppendUnique(_state.CustomBridges, row.Line, Environment.NewLine);
            _state.SelectedBridgeType = "custom";
            _state.BridgeSourceMode = AppStateViewModel.BridgeSourceCustom;
            _state.UseTorBridges = true;
            _state.AppendLog("Saved library: applied a saved bridge to your custom bridge list.");
            StatusText = "Applied saved bridge to your custom bridge list (Custom source).";
        }
    }

    [RelayCommand]
    private void Delete(SavedBridgeRow? row)
    {
        if (row == null)
        {
            return;
        }

        _store.Remove(row.Id);
        Refresh();
        StatusText = "Removed.";
    }

    [RelayCommand]
    private void Clear()
    {
        _store.Clear();
        Refresh();
        StatusText = "Library cleared.";
    }

    /// <summary>Persist an edited label from the row.</summary>
    public void CommitLabel(SavedBridgeRow? row)
    {
        if (row == null || string.IsNullOrEmpty(row.Id))
        {
            return;
        }

        _store.SetLabel(row.Id, row.Label ?? string.Empty);
    }

    private static string AppendUnique(string? existing, string line, string separator)
    {
        var current = existing ?? string.Empty;
        var parts = current
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Any(p => string.Equals(p, line.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return current;
        }

        parts.Add(line.Trim());
        return string.Join(separator, parts);
    }
}

public sealed partial class SavedBridgeRow : ObservableObject
{
    public SavedBridgeRow(SavedBridge entry)
    {
        Id = entry.Id;
        Line = entry.Line;
        Kind = entry.Kind;
        Transport = entry.Transport;
        Source = entry.Source;
        LastStatus = entry.LastStatus;
        _label = entry.Label;
        Added = FormatAdded(entry.AddedUtc);
        HasPing = entry.LastPingMs.HasValue;
        Ping = HasPing
            ? $"✔ {entry.LastPingMs!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms"
            : "—";
        // Green for a healthy latency, amber for a slow one - same tone language as the scanner.
        PingTone = string.Equals(entry.LastStatus, "slow", StringComparison.OrdinalIgnoreCase) ? "warning" : "success";
    }

    public string Id { get; }
    public string Line { get; }
    public string Kind { get; }
    public string Transport { get; }
    public string Source { get; }
    public string LastStatus { get; }
    public string Added { get; }

    /// <summary>Latency at save time as a badge ("✔ 289 ms"), shown green/amber; "—" when unknown.</summary>
    public string Ping { get; }
    public bool HasPing { get; }
    public string PingTone { get; }

    [ObservableProperty] private string _label = string.Empty;

    public bool IsSni => string.Equals(Kind, SavedBridgeKind.Sni, StringComparison.Ordinal);

    /// <summary>Short type badge: the transport for a bridge, or "SNI" for an SNI host.</summary>
    public string TypeLabel => IsSni ? "SNI" : (string.IsNullOrWhiteSpace(Transport) ? "bridge" : Transport);

    private static string FormatAdded(string iso)
    {
        return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;
    }
}

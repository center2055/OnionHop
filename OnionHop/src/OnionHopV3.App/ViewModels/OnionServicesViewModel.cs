using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

/// <summary>
/// Onion services section in Settings (#77). Lets the user publish something on their own network,
/// a camera, a NAS, a web app, under a `.onion` address: no port forwarding, no static IP or DynDNS,
/// and nothing exposed to the open internet where scanners find it.
///
/// Addresses are only assigned by Tor at connect time, so rows show "not published yet" until the
/// first successful connect, after which the saved key keeps the address the same.
/// </summary>
public sealed partial class OnionServicesViewModel : ObservableObject
{
    private readonly AppStateViewModel _state;
    private readonly OnionServiceStore _store;

    public OnionServicesViewModel(AppStateViewModel state, OnionServiceStore store)
    {
        _state = state;
        _store = store;
        Refresh();
    }

    public ObservableCollection<OnionServiceRow> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private int _count;

    [ObservableProperty] private string _statusText = string.Empty;

    // New-entry fields.
    [ObservableProperty] private string _newLabel = string.Empty;
    [ObservableProperty] private string _newOnionPort = "80";
    [ObservableProperty] private string _newTargetHost = "127.0.0.1";
    [ObservableProperty] private string _newTargetPort = "80";

    public bool HasItems => Count > 0;

    public void Refresh()
    {
        Items.Clear();
        foreach (var entry in _store.Load())
        {
            Items.Add(new OnionServiceRow(entry));
        }

        Count = Items.Count;
    }

    public void NotifyCopied() => StatusText = "Address copied to clipboard.";

    [RelayCommand]
    private void Add()
    {
        if (!TryParsePort(NewOnionPort, out var onionPort))
        {
            StatusText = "The onion port must be a number between 1 and 65535.";
            return;
        }

        if (!TryParsePort(NewTargetPort, out var targetPort))
        {
            StatusText = "The target port must be a number between 1 and 65535.";
            return;
        }

        var host = string.IsNullOrWhiteSpace(NewTargetHost) ? "127.0.0.1" : NewTargetHost.Trim();

        var service = new OnionService
        {
            Label = NewLabel.Trim(),
            OnionPort = onionPort,
            TargetHost = host,
            TargetPort = targetPort,
            Enabled = true
        };

        _store.Add(service);
        Refresh();

        _state.AppendLog(
            $"Onion service added: {(service.Label.Length > 0 ? service.Label : host)} " +
            $"(port {onionPort} -> {host}:{targetPort}). It gets its address on the next connect.");
        StatusText = "Added. Connect to publish it and get the address.";

        NewLabel = string.Empty;
        NewOnionPort = "80";
        NewTargetHost = "127.0.0.1";
        NewTargetPort = "80";
    }

    [RelayCommand]
    private void Remove(OnionServiceRow? row)
    {
        if (row == null)
        {
            return;
        }

        _store.Remove(row.Id);
        Refresh();
        _state.AppendLog($"Onion service removed: {row.Display}. Its address is gone for good.");
        StatusText = "Removed.";
    }

    [RelayCommand]
    private void ToggleEnabled(OnionServiceRow? row)
    {
        if (row == null)
        {
            return;
        }

        var entry = _store.Load().FirstOrDefault(s => string.Equals(s.Id, row.Id, StringComparison.Ordinal));
        if (entry == null)
        {
            return;
        }

        entry.Enabled = !entry.Enabled;
        _store.Update(entry);
        Refresh();
        StatusText = entry.Enabled
            ? "Enabled. It publishes on the next connect."
            : "Disabled. It will not be published.";
    }

    internal static bool TryParsePort(string? raw, out int port)
    {
        port = 0;
        return int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }
}

/// <summary>One row in the onion services list.</summary>
public sealed partial class OnionServiceRow : ObservableObject
{
    public OnionServiceRow(OnionService entry)
    {
        Id = entry.Id;
        Label = entry.Label;
        OnionPort = entry.OnionPort;
        TargetHost = entry.TargetHost;
        TargetPort = entry.TargetPort;
        Hostname = entry.Hostname;
        IsEnabled = entry.Enabled;
    }

    public string Id { get; }
    public string Label { get; }
    public int OnionPort { get; }
    public string TargetHost { get; }
    public int TargetPort { get; }

    /// <summary>Full `.onion` hostname, or empty until Tor has published it once.</summary>
    public string Hostname { get; }

    public bool IsEnabled { get; }

    public bool IsPublished => Hostname.Length > 0;

    public string Display => Label.Length > 0 ? Label : $"{TargetHost}:{TargetPort}";

    /// <summary>What callers connect to, or a placeholder before the first publish.</summary>
    public string AddressText => IsPublished
        ? (OnionPort == 80 ? Hostname : $"{Hostname}:{OnionPort}")
        : "Not published yet. Connect to get the address.";

    public string ForwardText => $"{TargetHost}:{TargetPort}";
}

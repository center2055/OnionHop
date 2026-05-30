using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV3.App.Services;

namespace OnionHopV3.App.ViewModels;

/// <summary>One running app row in the split-tunnel picker.</summary>
public sealed partial class AppRouteItemViewModel : ObservableObject
{
    public AppRouteItemViewModel(string executableName, string displayName, IReadOnlyList<string> modeOptions, int mode)
    {
        ExecutableName = executableName;
        DisplayName = displayName;
        ModeOptions = modeOptions;
        _mode = mode;
    }

    public string ExecutableName { get; }
    public string DisplayName { get; }

    /// <summary>Localized labels for the per-app routing choice, indexed by mode (Default/Tor/Direct).</summary>
    public IReadOnlyList<string> ModeOptions { get; }

    [ObservableProperty] private int _mode;
}

/// <summary>Backs the split-tunnel app picker dialog: lists running apps and tracks per-app routing choices.</summary>
public sealed partial class AppPickerViewModel : ObservableObject
{
    public const int ModeDefault = 0;
    public const int ModeTor = 1;
    public const int ModeDirect = 2;

    private readonly IReadOnlyList<string> _modeOptions;
    private readonly HashSet<string> _torApps;
    private readonly HashSet<string> _bypassApps;

    public AppPickerViewModel(
        IReadOnlyList<string> modeOptions,
        IEnumerable<string> torApps,
        IEnumerable<string> bypassApps,
        IReadOnlyList<RunningAppInfo> initialApps)
    {
        _modeOptions = modeOptions;
        _torApps = new HashSet<string>(torApps, StringComparer.OrdinalIgnoreCase);
        _bypassApps = new HashSet<string>(bypassApps, StringComparer.OrdinalIgnoreCase);
        Populate(initialApps);
    }

    public ObservableCollection<AppRouteItemViewModel> Apps { get; } = new();

    [ObservableProperty] private bool _isEmpty;

    // The scan reads each process's module info from disk, so run it off the UI thread; the
    // await resumes on the UI thread (Avalonia sync context), where the collection is rebuilt.
    [RelayCommand]
    private async Task Rescan()
    {
        var apps = await Task.Run(RunningAppsService.GetRunningApps).ConfigureAwait(true);
        Populate(apps);
    }

    private void Populate(IReadOnlyList<RunningAppInfo> apps)
    {
        // Preserve any choices the user already made this session, keyed by executable name.
        var priorChoices = Apps.ToDictionary(a => a.ExecutableName, a => a.Mode, StringComparer.OrdinalIgnoreCase);

        Apps.Clear();
        foreach (var app in apps)
        {
            int mode;
            if (priorChoices.TryGetValue(app.ExecutableName, out var prior))
            {
                mode = prior;
            }
            else if (_torApps.Contains(app.ExecutableName))
            {
                mode = ModeTor;
            }
            else if (_bypassApps.Contains(app.ExecutableName))
            {
                mode = ModeDirect;
            }
            else
            {
                mode = ModeDefault;
            }

            Apps.Add(new AppRouteItemViewModel(app.ExecutableName, app.DisplayName, _modeOptions, mode));
        }

        IsEmpty = Apps.Count == 0;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using OnionHopV3.App.Services;
using OnionHopV3.App.ViewModels;

namespace OnionHopV3.App.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
    }

    private async void OnPickAppsClick(object? sender, RoutedEventArgs e)
    {
        var state = (DataContext as PageViewModelBase)?.State;
        if (state == null)
        {
            return;
        }

        try
        {
            var modeOptions = new[]
            {
                LocalizationService.Get("Settings.AppPickerModeDefault"),
                LocalizationService.Get("Settings.AppPickerModeTor"),
                LocalizationService.Get("Settings.AppPickerModeDirect"),
            };

            // Scan off the UI thread so reading process module info never freezes the window.
            var apps = await Task.Run(RunningAppsService.GetRunningApps).ConfigureAwait(true);

            var picker = new AppPickerViewModel(
                modeOptions,
                HybridAppList.Parse(state.HybridTorApps),
                HybridAppList.Parse(state.HybridBypassApps),
                apps);

            var dialog = new ContentDialog
            {
                Title = LocalizationService.Get("Settings.AppPickerTitle"),
                PrimaryButtonText = LocalizationService.Get("Common.Apply"),
                CloseButtonText = LocalizationService.Get("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new AppPickerView { DataContext = picker }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // Apps shown in the picker are removed from both lists and re-added per the chosen mode;
            // names that aren't currently running (e.g. hand-typed earlier) are preserved untouched.
            var shown = new HashSet<string>(picker.Apps.Select(a => a.ExecutableName), StringComparer.OrdinalIgnoreCase);
            var tor = HybridAppList.Parse(state.HybridTorApps).Where(n => !shown.Contains(n)).ToList();
            var bypass = HybridAppList.Parse(state.HybridBypassApps).Where(n => !shown.Contains(n)).ToList();

            foreach (var item in picker.Apps)
            {
                if (item.Mode == AppPickerViewModel.ModeTor)
                {
                    tor.Add(item.ExecutableName);
                }
                else if (item.Mode == AppPickerViewModel.ModeDirect)
                {
                    bypass.Add(item.ExecutableName);
                }
            }

            state.HybridTorApps = HybridAppList.Join(tor);
            state.HybridBypassApps = HybridAppList.Join(bypass);
        }
        catch
        {
            // Never let a picker/dialog failure take down the Settings page.
        }
    }
}

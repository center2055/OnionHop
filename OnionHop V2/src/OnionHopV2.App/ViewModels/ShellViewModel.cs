using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace OnionHopV2.App.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    public ShellViewModel()
    {
        State = new AppStateViewModel();

        Pages =
        [
            new HomePageViewModel(State),
            new SettingsPageViewModel(State),
            new LogsPageViewModel(State),
            new AboutPageViewModel(State)
        ];

        ActivePage = Pages[0];
    }

    public AppStateViewModel State { get; }

    public ObservableCollection<PageViewModelBase> Pages { get; }

    [ObservableProperty] private PageViewModelBase _activePage = null!;

    public void Dispose()
    {
        State.Dispose();
    }
}

public sealed class HomePageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Home", MaterialIconKind.HomeOutline, state);

public sealed class SettingsPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Settings", MaterialIconKind.CogOutline, state);

public sealed class LogsPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Logs", MaterialIconKind.TextBoxOutline, state);

public sealed class AboutPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.About", MaterialIconKind.InformationOutline, state);

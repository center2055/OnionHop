using Material.Icons;

namespace OnionHopV3.App.ViewModels;

public sealed class SettingsPageViewModel(AppStateViewModel state)
    : PageViewModelBase("Nav.Settings", MaterialIconKind.CogOutline, state, 0xE713);

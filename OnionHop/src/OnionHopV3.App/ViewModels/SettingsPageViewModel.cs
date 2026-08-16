using Material.Icons;
using OnionHopV3.Core.Services;

namespace OnionHopV3.App.ViewModels;

public sealed class SettingsPageViewModel : PageViewModelBase
{
    public SettingsPageViewModel(AppStateViewModel state)
        : base("Nav.Settings", MaterialIconKind.CogOutline, state, 0xE713)
    {
        OnionServices = new OnionServicesViewModel(state, new OnionServiceStore());
    }

    /// <summary>Onion services the user publishes from this machine (#77).</summary>
    public OnionServicesViewModel OnionServices { get; }
}

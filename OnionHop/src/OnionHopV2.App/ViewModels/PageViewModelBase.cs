using Material.Icons;
using OnionHopV2.App.Services;

namespace OnionHopV2.App.ViewModels;

public abstract class PageViewModelBase : ViewModelBase
{
    private readonly string _displayNameKey;

    protected PageViewModelBase(string displayNameKey, MaterialIconKind icon, AppStateViewModel state)
    {
        _displayNameKey = displayNameKey;
        Icon = icon;
        State = state;
        LocalizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(DisplayName));
    }

    public string DisplayName => LocalizationService.Get(_displayNameKey);
    public MaterialIconKind Icon { get; }
    public AppStateViewModel State { get; }
}

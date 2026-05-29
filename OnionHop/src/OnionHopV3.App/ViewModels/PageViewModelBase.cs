using Material.Icons;
using Avalonia;
using Avalonia.Media;
using OnionHopV3.App.Services;

namespace OnionHopV3.App.ViewModels;

public abstract class PageViewModelBase : ViewModelBase
{
    private readonly string _displayNameKey;
    private bool _isActive;

    protected PageViewModelBase(string displayNameKey, MaterialIconKind icon, AppStateViewModel state, int navGlyphCode = 0)
    {
        _displayNameKey = displayNameKey;
        Icon = icon;
        NavGlyph = navGlyphCode > 0 ? char.ConvertFromUtf32(navGlyphCode) : string.Empty;
        State = state;
        LocalizationService.LanguageChanged += (_, _) => OnPropertyChanged(nameof(DisplayName));
        State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppStateViewModel.SelectedAccentColor))
            {
                OnPropertyChanged(nameof(NavBackgroundBrush));
                OnPropertyChanged(nameof(NavBorderBrush));
                OnPropertyChanged(nameof(NavIconBrush));
            }
        };
    }

    public string DisplayName => LocalizationService.Get(_displayNameKey);
    public MaterialIconKind Icon { get; }

    /// <summary>Segoe Fluent / MDL2 glyph for the NavigationView item icon (empty = none).</summary>
    public string NavGlyph { get; }
    public AppStateViewModel State { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(NavBackgroundBrush));
                OnPropertyChanged(nameof(NavBorderBrush));
                OnPropertyChanged(nameof(NavTextBrush));
                OnPropertyChanged(nameof(NavIconBrush));
                OnPropertyChanged(nameof(NavFontWeight));
            }
        }
    }

    public IBrush NavBackgroundBrush => IsActive
        ? ResourceBrush("AccentSoftBrush", "#2AA970FF")
        : Brushes.Transparent;

    public IBrush NavBorderBrush => IsActive
        ? ResourceBrush("AccentOutlineBrush", "#4AA970FF")
        : Brushes.Transparent;

    public IBrush NavTextBrush => IsActive
        ? ResourceBrush("TextPrimaryBrush", "#F4F7FF")
        : ResourceBrush("TextSecondaryBrush", "#C4CEE8");

    public IBrush NavIconBrush => IsActive
        ? ResourceBrush("AccentPrimaryBrush", "#A970FF")
        : ResourceBrush("TextMutedBrush", "#8F9AB7");

    public FontWeight NavFontWeight => IsActive ? FontWeight.SemiBold : FontWeight.Normal;

    public virtual void OnActivated()
    {
    }

    private static IBrush ResourceBrush(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallback));
    }
}

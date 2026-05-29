using Avalonia;
using Avalonia.Controls;
using Material.Icons;

namespace OnionHopV2.App.Controls;

public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<EmptyState, MaterialIconKind>(nameof(Icon), MaterialIconKind.InformationOutline);

    public EmptyState()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public MaterialIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

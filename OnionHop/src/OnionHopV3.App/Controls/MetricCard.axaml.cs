using Avalonia;
using Avalonia.Controls;

namespace OnionHopV3.App.Controls;

public partial class MetricCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<MetricCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<MetricCard, string>(nameof(Value), string.Empty);

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<MetricCard, string>(nameof(Caption), string.Empty);

    public MetricCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }
}

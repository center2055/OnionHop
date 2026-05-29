using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace OnionHopV3.App.Controls;

public partial class ToggleRow : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ToggleRow, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<ToggleRow, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ToggleRow, bool>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public ToggleRow()
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

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}

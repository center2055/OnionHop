using Avalonia;
using Avalonia.Controls;

namespace OnionHopV2.App.Controls;

public partial class SettingRow : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<object?> RowContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(RowContent));

    public SettingRow()
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

    public object? RowContent
    {
        get => GetValue(RowContentProperty);
        set => SetValue(RowContentProperty, value);
    }
}

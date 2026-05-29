using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OnionHopV3.App.Controls;

public partial class StatusBadge : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> ToneProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(Tone), "neutral");

    public StatusBadge()
    {
        InitializeComponent();
        UpdateTone();
    }

    static StatusBadge()
    {
        ToneProperty.Changed.AddClassHandler<StatusBadge>((badge, _) => badge.UpdateTone());
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    private void UpdateTone()
    {
        var tone = Tone?.Trim().ToLowerInvariant() ?? "neutral";
        var backgroundKey = tone switch
        {
            "success" => "SuccessSoftBrush",
            "warning" => "WarningSoftBrush",
            "danger" => "DangerSoftBrush",
            "info" => "InfoSoftBrush",
            "accent" => "AccentSoftBrush",
            _ => "SurfaceRaisedBrush"
        };

        var foregroundKey = tone switch
        {
            "warning" => "WarningBrush",
            "danger" => "DangerBrush",
            "info" => "InfoBrush",
            "success" => "SuccessBrush",
            "accent" => "AccentPrimaryBrush",
            _ => "TextSecondaryBrush"
        };

        BadgeBorder.Background = GetBrush(backgroundKey);
        BadgeBorder.BorderBrush = GetBrush(foregroundKey);
        // Flat, Fluent-style soft pill — the dot + soft fill carry the status, no heavy outline.
        BadgeBorder.BorderThickness = new Thickness(0);
        BadgeText.Foreground = GetBrush(tone == "neutral" ? "TextSecondaryBrush" : "TextPrimaryBrush");
        Dot.Fill = GetBrush(foregroundKey);
    }

    private IBrush GetBrush(string key)
    {
        return (Application.Current?.TryFindResource(key, out var resource) == true
                ? resource as IBrush
                : null)
               ?? Brushes.Gray;
    }
}

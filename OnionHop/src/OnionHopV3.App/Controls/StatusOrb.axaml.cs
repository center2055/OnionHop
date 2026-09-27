using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace OnionHopV3.App.Controls;

/// <summary>
/// Circular status indicator: a soft tone-coloured disc with a tone-coloured glyph. The brushes are
/// bound to theme resources rather than looked up once, so switching light/dark (or the accent
/// following Windows) recolours it without waiting for the state to change.
/// </summary>
public partial class StatusOrb : UserControl
{
    public static readonly StyledProperty<string> ToneProperty =
        AvaloniaProperty.Register<StatusOrb, string>(nameof(Tone), "neutral");

    public static readonly StyledProperty<MaterialIconKind> KindProperty =
        AvaloniaProperty.Register<StatusOrb, MaterialIconKind>(nameof(Kind), MaterialIconKind.ShieldOffOutline);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StatusOrb, double>(nameof(Size), 64);

    public static readonly DirectProperty<StatusOrb, double> GlyphSizeProperty =
        AvaloniaProperty.RegisterDirect<StatusOrb, double>(nameof(GlyphSize), orb => orb.GlyphSize);

    private IDisposable? _backgroundBinding;
    private IDisposable? _glyphBinding;

    public StatusOrb()
    {
        InitializeComponent();
        ApplyTone();
    }

    static StatusOrb()
    {
        ToneProperty.Changed.AddClassHandler<StatusOrb>((orb, _) => orb.ApplyTone());
        SizeProperty.Changed.AddClassHandler<StatusOrb>((orb, e) =>
            orb.RaisePropertyChanged(GlyphSizeProperty, 0d, orb.GlyphSize));
    }

    public string Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public MaterialIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>The glyph fills a bit over half the disc, like a Windows Security status icon.</summary>
    public double GlyphSize => Math.Round(Size * 0.52);

    internal static (string Soft, string Strong) BrushKeysFor(string? tone) =>
        (tone?.Trim().ToLowerInvariant()) switch
        {
            "success" => ("SuccessSoftBrush", "SuccessBrush"),
            "warning" => ("WarningSoftBrush", "WarningBrush"),
            "danger" => ("DangerSoftBrush", "DangerBrush"),
            "info" => ("InfoSoftBrush", "InfoBrush"),
            "accent" => ("AccentSoftBrush", "AccentPrimaryBrush"),
            _ => ("SurfaceRaisedBrush", "TextSecondaryBrush")
        };

    private void ApplyTone()
    {
        var (soft, strong) = BrushKeysFor(Tone);

        _backgroundBinding?.Dispose();
        _glyphBinding?.Dispose();
        _backgroundBinding = Disc.Bind(Border.BackgroundProperty, Disc.GetResourceObservable(soft));
        _glyphBinding = Glyph.Bind(TemplatedControl.ForegroundProperty, Glyph.GetResourceObservable(strong));
    }
}

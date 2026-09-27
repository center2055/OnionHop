using System;
using Avalonia;
using Avalonia.Controls;

namespace OnionHopV3.App.Controls;

/// <summary>
/// A small tone-coloured dot, the same language as StatusBadge's dot, for places that only need the
/// colour: e.g. the Home "Latest" line, where a failed connect should read as a failure at a glance.
/// </summary>
public sealed class ToneDot : Border
{
    public static readonly StyledProperty<string> ToneProperty =
        AvaloniaProperty.Register<ToneDot, string>(nameof(Tone), "neutral");

    private IDisposable? _fillBinding;

    static ToneDot()
    {
        ToneProperty.Changed.AddClassHandler<ToneDot>((dot, _) => dot.ApplyTone());
    }

    public ToneDot()
    {
        Width = 8;
        Height = 8;
        CornerRadius = new CornerRadius(4);
        ApplyTone();
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public string Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    private void ApplyTone()
    {
        _fillBinding?.Dispose();
        _fillBinding = Bind(BackgroundProperty, this.GetResourceObservable(StatusOrb.BrushKeysFor(Tone).Strong));
    }
}

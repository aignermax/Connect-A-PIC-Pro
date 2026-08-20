using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;

namespace CAP.Avalonia.Controls;

/// <summary>
/// The Logic panel's waveform strip (issue #1129, rung 5 visualizer): a
/// fixed-height, read-only view drawing the event timeline as per-signal 0/1
/// traces — one lane per named signal, vertical lines at the clock boundaries, a
/// cursor at the replayed instant. The control is a thin Avalonia host for
/// <see cref="LogicWaveformRenderer"/>: its <see cref="Waveform"/> property carries
/// the already-mapped model, the height follows the lane count, and every model
/// change repaints. No scrolling, zooming, or hit-testing in this slice.
/// </summary>
public class LogicWaveformStrip : Control
{
    /// <summary>The waveform model to draw; null draws nothing.</summary>
    public static readonly StyledProperty<LogicWaveformModel?> WaveformProperty =
        AvaloniaProperty.Register<LogicWaveformStrip, LogicWaveformModel?>(nameof(Waveform));

    static LogicWaveformStrip()
    {
        AffectsRender<LogicWaveformStrip>(WaveformProperty);
        AffectsMeasure<LogicWaveformStrip>(WaveformProperty);
    }

    /// <summary>The waveform model to draw; null draws nothing.</summary>
    public LogicWaveformModel? Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, LogicWaveformRenderer.DesiredHeight(Waveform));
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Waveform != null)
            LogicWaveformRenderer.Render(context, Waveform, Bounds.Size);
    }
}

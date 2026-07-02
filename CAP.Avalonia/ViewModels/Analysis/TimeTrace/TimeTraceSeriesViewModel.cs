using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;

namespace CAP.Avalonia.ViewModels.Analysis.TimeTrace;

/// <summary>
/// One entry in the transient-plot legend: a single output-pin trace.
/// Carries the pin identity, its display label, the colour used to draw it,
/// and a user-toggleable visibility flag. The owning ViewModel observes
/// <see cref="ObservableObject.PropertyChanged"/> to rebuild the plot when
/// <see cref="IsVisible"/> changes.
/// </summary>
public partial class TimeTraceSeriesViewModel : ObservableObject
{
    /// <summary>Outflow pin Guid this series was built from.</summary>
    public Guid PinId { get; }

    /// <summary>Human-readable label shown in the legend (e.g. "MMI.out1").</summary>
    public string Label { get; }

    /// <summary>Colour used for both the legend swatch and the plotted line.</summary>
    public OxyColor Color { get; }

    /// <summary>Hex string of <see cref="Color"/> for AXAML brush binding.</summary>
    public string ColorHex => Color.ToString();

    /// <summary>
    /// Whether this trace is drawn. Toggling it triggers a plot rebuild in the
    /// owning ViewModel via property-changed notification.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Initializes a new legend entry for one output-pin trace.</summary>
    /// <param name="pinId">Outflow pin Guid.</param>
    /// <param name="label">Display label for the legend.</param>
    /// <param name="color">Series colour.</param>
    public TimeTraceSeriesViewModel(Guid pinId, string label, OxyColor color)
    {
        PinId = pinId;
        Label = label;
        Color = color;
    }
}

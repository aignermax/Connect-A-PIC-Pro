using CAP_Core.ExternalPorts.LaserSpectrum;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Simulation;

/// <summary>
/// Selectable laser line shape for the light-source editor (Issue #819).
/// Labels are resolved through the localization service when the list is built.
/// </summary>
public class LineShapeOption
{
    /// <summary>The line-shape value this option selects.</summary>
    public LaserLineShape Value { get; }

    /// <summary>Localized display label.</summary>
    public string Label { get; }

    public LineShapeOption(LaserLineShape value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>Builds the option list with labels in the current UI language.</summary>
    public static IReadOnlyList<LineShapeOption> CreateAll()
    {
        var loc = LocalizationService.Instance;
        return new[]
        {
            new LineShapeOption(LaserLineShape.Ideal, loc.Translate("SelectedProps.LineShapeIdeal")),
            new LineShapeOption(LaserLineShape.Gaussian, loc.Translate("SelectedProps.LineShapeGaussian")),
            new LineShapeOption(LaserLineShape.Lorentzian, loc.Translate("SelectedProps.LineShapeLorentzian")),
        };
    }

    public override string ToString() => Label;
}

using System.Globalization;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// Renders a <see cref="NonConvergentCircuitException"/> as a fully localized status
/// message (field round 4, final batch). The core exception carries structured
/// diagnostics (kind, loop component names, culprit component, wavelength, energy
/// excess); this formatter maps them onto the <c>Analysis.Circuit.*</c> string keys in
/// all shipped languages. When the structured data is incomplete it falls back to the
/// exception's English message inside the generic localized "Failed: {0}" wrapper.
/// </summary>
public static class NonConvergentCircuitMessageFormatter
{
    /// <summary>Formats the exception as a localized, user-facing status message.</summary>
    /// <param name="exception">The rejected-circuit exception thrown by the closure solve.</param>
    public static string Format(NonConvergentCircuitException exception)
    {
        var loc = LocalizationService.Instance;
        var inv = CultureInfo.InvariantCulture;

        if (exception.Kind == NonConvergentCircuitKind.ResonantLoop
            && exception.LoopComponentNames is { Count: > 0 } loopNames
            && exception.WavelengthNm is int loopNm)
        {
            return string.Format(
                loc.Translate("Analysis.Circuit.ResonantLoop"),
                string.Join(" ↔ ", loopNames),
                loopNm.ToString(inv));
        }

        if (exception.Kind == NonConvergentCircuitKind.NonPassiveComponent
            && exception.ComponentName is { } componentName
            && exception.ExcessPercent is double passivityExcess
            && exception.WavelengthNm is int passivityNm)
        {
            return string.Format(
                loc.Translate("Analysis.Circuit.NonPassiveComponent"),
                componentName,
                passivityExcess.ToString("F1", inv),
                passivityNm.ToString(inv));
        }

        if (exception.Kind == NonConvergentCircuitKind.EnergyFabricated
            && exception.ExcessPercent is double energyExcess
            && exception.WavelengthNm is int energyNm)
        {
            return string.Format(
                loc.Translate("Analysis.Circuit.EnergyFabricated"),
                energyExcess.ToString("F1", inv),
                energyNm.ToString(inv));
        }

        return string.Format(loc.Translate("Analysis.Common.Failed"), exception.Message);
    }
}

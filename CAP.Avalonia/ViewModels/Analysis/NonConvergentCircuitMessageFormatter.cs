using System.Globalization;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// Renders a <see cref="NonConvergentCircuitException"/> as a fully localized status
/// message (field round 4, final batch). The core exception carries structured
/// diagnostics (kind, loop component names, culprit component, wavelength, energy
/// excess); this formatter maps them onto the <c>Analysis.Circuit.*</c> string keys in
/// all shipped languages. Numbers are formatted in the culture of the ACTIVE UI
/// language (review finding [5]) so a German sentence shows "2,1 %", not "2.1".
/// When the structured data is incomplete it falls back to the exception's English
/// message inside the generic localized "Failed: {0}" wrapper.
/// </summary>
public static class NonConvergentCircuitMessageFormatter
{
    /// <summary>Formats the exception as a localized, user-facing status message.</summary>
    /// <param name="exception">The rejected-circuit exception thrown by the closure solve.</param>
    public static string Format(NonConvergentCircuitException exception)
    {
        var loc = LocalizationService.Instance;
        var culture = CultureInfo.GetCultureInfo(loc.ActiveLanguageCode);

        string? message = exception.Kind switch
        {
            NonConvergentCircuitKind.ResonantLoop => FormatResonantLoop(exception, loc, culture),
            NonConvergentCircuitKind.NonPassiveComponent => FormatNonPassive(exception, loc, culture),
            NonConvergentCircuitKind.ConnectionGain => FormatConnectionGain(exception, loc, culture),
            NonConvergentCircuitKind.EnergyFabricated => FormatEnergyFabricated(exception, loc, culture),
            _ => null,
        };
        return message ?? string.Format(loc.Translate("Analysis.Common.Failed"), exception.Message);
    }

    /// <summary>
    /// Localized resonant-loop message; loops whose components could not be named get
    /// their own localized variant (review finding [9]) instead of raw English.
    /// </summary>
    private static string? FormatResonantLoop(
        NonConvergentCircuitException exception, LocalizationService loc, CultureInfo culture)
    {
        if (exception.WavelengthNm is not int wavelengthNm)
            return null;
        if (exception.LoopComponentNames is { Count: > 0 } loopNames)
        {
            return string.Format(
                loc.Translate("Analysis.Circuit.ResonantLoop"),
                FeedbackLoopFinder.Describe(loopNames),
                wavelengthNm.ToString(culture));
        }
        return string.Format(
            loc.Translate("Analysis.Circuit.ResonantLoopUnnamed"),
            wavelengthNm.ToString(culture));
    }

    private static string? FormatNonPassive(
        NonConvergentCircuitException exception, LocalizationService loc, CultureInfo culture)
    {
        if (exception.ComponentName is not { } componentName
            || exception.ExcessPercent is not double excess
            || exception.WavelengthNm is not int wavelengthNm)
        {
            return null;
        }
        return string.Format(
            loc.Translate("Analysis.Circuit.NonPassiveComponent"),
            componentName,
            excess.ToString("F1", culture),
            wavelengthNm.ToString(culture));
    }

    private static string? FormatConnectionGain(
        NonConvergentCircuitException exception, LocalizationService loc, CultureInfo culture)
    {
        if (exception.ComponentName is not { } connectionName
            || exception.ExcessPercent is not double excess
            || exception.WavelengthNm is not int wavelengthNm)
        {
            return null;
        }
        return string.Format(
            loc.Translate("Analysis.Circuit.ConnectionGain"),
            connectionName,
            excess.ToString("F1", culture),
            wavelengthNm.ToString(culture));
    }

    private static string? FormatEnergyFabricated(
        NonConvergentCircuitException exception, LocalizationService loc, CultureInfo culture)
    {
        if (exception.ExcessPercent is not double excess
            || exception.WavelengthNm is not int wavelengthNm)
        {
            return null;
        }
        return string.Format(
            loc.Translate("Analysis.Circuit.EnergyFabricated"),
            excess.ToString("F1", culture),
            wavelengthNm.ToString(culture));
    }
}

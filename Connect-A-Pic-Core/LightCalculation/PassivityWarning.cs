using System.Globalization;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Emitted through <see cref="TransitiveClosureContext.PassivityWarningSink"/> when a
/// component's S-matrix block exceeds passivity WITHIN the shipped measurement-noise
/// band (<see cref="SingleHopPassivityChecker.MeasuredDataNoiseBand"/>): the run
/// continues, but transmission may be overestimated by up to
/// <see cref="ExcessPercent"/> percent (field round 4 final review, finding [1]).
/// </summary>
/// <param name="ComponentName">Display name of the component whose block is noisy.</param>
/// <param name="WavelengthNm">Wavelength the closure was computed for, when known.</param>
/// <param name="ExcessPercent">Passivity excess in percent: (σ_max − 1) · 100.</param>
public sealed record PassivityWarning(string ComponentName, int? WavelengthNm, double ExcessPercent)
{
    /// <summary>English log/console message (invariant numbers — log convention).</summary>
    public string ToMessage()
    {
        string excess = ExcessPercent.ToString("F2", CultureInfo.InvariantCulture);
        string band = (SingleHopPassivityChecker.MeasuredDataNoiseBand * 100.0)
            .ToString("F1", CultureInfo.InvariantCulture);
        string wavelengthClause = WavelengthNm is int nm
            ? $" at {nm.ToString(CultureInfo.InvariantCulture)} nm"
            : "";
        return $"Component '{ComponentName}' S-matrix exceeds passivity by {excess} %{wavelengthClause} — " +
               $"within the shipped measurement-noise band (≤ {band} %): treated as measurement noise; " +
               $"results may overestimate transmission by up to {excess} %.";
    }
}

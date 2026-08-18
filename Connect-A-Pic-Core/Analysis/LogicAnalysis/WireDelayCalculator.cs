using CAP_Core.Components.Connections;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Derives the propagation delay of one inter-gate waveguide from its routed path:
/// geometric length · n_g / c — the same convention and dispersion-model fallback as
/// <see cref="GateDelayCalculator"/>: the connection's dispersion model supplies the
/// group index when it carries one, else <see cref="GateDelayCalculator.DefaultGroupIndex"/>
/// (a silicon strip waveguide). A connection without a routed path (direct-adjacent
/// pins) contributes zero delay; the result never goes negative.
/// </summary>
public sealed class WireDelayCalculator
{
    /// <summary>
    /// Computes the wire's propagation delay in picoseconds from its routed path length.
    /// </summary>
    /// <param name="connection">The waveguide connection joining two gate pins.</param>
    /// <param name="wavelengthNm">
    /// Wavelength in nm the group index is evaluated at (only relevant when the
    /// connection carries a wavelength-dependent dispersion model).
    /// </param>
    /// <returns>The delay light needs to cross the wire, in picoseconds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public double CalculatePicoseconds(WaveguideConnection connection, double wavelengthNm)
    {
        if (connection == null) throw new ArgumentNullException(nameof(connection));
        var groupIndex = connection.DispersionModel?.GroupIndexAt(wavelengthNm)
            ?? GateDelayCalculator.DefaultGroupIndex;
        var delay = connection.PathLengthMicrometers * groupIndex
            / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond;
        return Math.Max(0, delay);
    }
}

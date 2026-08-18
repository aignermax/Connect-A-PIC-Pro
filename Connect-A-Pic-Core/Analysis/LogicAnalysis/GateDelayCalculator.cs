using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Derives the propagation delay of one logic gate from its group's internal optical
/// path length: the geometric lengths of the group's internal waveguide paths plus the
/// physical width of every leaf child component (the distance light travels through it
/// along the propagation axis), converted with the waveguide group index —
/// delay = L · n_g / c. The group index comes from an internal path's dispersion model
/// when the process data carries one; otherwise <see cref="DefaultGroupIndex"/> applies
/// (a silicon strip waveguide). The result is a physically plausible estimate — µm of
/// path times n_g/c lands in the fs–ps range — not an exact timing model: per-edge wire
/// delays between gates and event-driven simulation are a later rung.
/// </summary>
public sealed class GateDelayCalculator
{
    /// <summary>
    /// Group index of a standard silicon strip waveguide, used when none of the group's
    /// internal waveguides carries dispersion data.
    /// </summary>
    public const double DefaultGroupIndex = 4.2;

    /// <summary>Speed of light in micrometers per picosecond.</summary>
    public const double SpeedOfLightMicrometersPerPicosecond = 299.792458;

    /// <summary>
    /// Computes the gate's propagation delay in picoseconds from its group's geometry.
    /// </summary>
    /// <param name="group">The gate group whose internal optical path length sets the delay.</param>
    /// <param name="wavelengthNm">
    /// Wavelength in nm the group index is evaluated at (only relevant when an internal
    /// waveguide carries a wavelength-dependent dispersion model).
    /// </param>
    /// <returns>The delay light needs to cross the gate, in picoseconds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    public double CalculatePicoseconds(ComponentGroup group, double wavelengthNm)
    {
        if (group == null) throw new ArgumentNullException(nameof(group));
        var lengthMicrometers = InternalPathLengthMicrometers(group);
        var groupIndex = ResolveGroupIndex(group, wavelengthNm);
        return lengthMicrometers * groupIndex / SpeedOfLightMicrometersPerPicosecond;
    }

    /// <summary>
    /// Total internal optical path length in micrometers: the geometric lengths of the
    /// group's internal waveguide paths plus the width of every leaf child component.
    /// </summary>
    public static double InternalPathLengthMicrometers(ComponentGroup group) =>
        group.InternalPaths.Sum(path => path.Path?.TotalLengthMicrometers ?? 0)
        + group.GetAllComponentsRecursive()
            .Where(component => component is not ComponentGroup)
            .Sum(component => component.WidthMicrometers);

    /// <summary>The first group index the group's internal waveguides carry, else the default.</summary>
    private static double ResolveGroupIndex(ComponentGroup group, double wavelengthNm) =>
        group.InternalPaths
            .Select(path => path.DispersionModel?.GroupIndexAt(wavelengthNm))
            .FirstOrDefault(index => index.HasValue)
        ?? DefaultGroupIndex;
}

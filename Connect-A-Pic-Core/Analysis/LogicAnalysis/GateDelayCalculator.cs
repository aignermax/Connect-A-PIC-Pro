using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Derives the propagation delay of one logic gate from its group's internal optical
/// path length: the geometric lengths of the group's internal waveguide paths plus the
/// physical width of every leaf child component (the distance light travels through it
/// along the propagation axis), converted with the waveguide group index —
/// delay = L · n_g / c. Nested <see cref="ComponentGroup"/> children contribute their
/// own paths and widths, so hierarchy does not change the physics. Each path is
/// converted with its own dispersion model's group index, falling back to
/// <see cref="DefaultGroupIndex"/>; component widths use the first group index the
/// recursive paths carry, else the default (a silicon strip waveguide). The result is
/// a physically plausible estimate — µm of path times n_g/c lands in the fs–ps range —
/// not an exact timing model: event-driven simulation is a later rung. The waveguides
/// between gates contribute their own per-edge delays, see <see cref="WireDelayCalculator"/>.
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
        var fallbackIndex = ResolveGroupIndex(group, wavelengthNm);
        var widthDelay = group.GetAllComponentsRecursive()
            .Where(component => component is not ComponentGroup)
            .Sum(component => component.WidthMicrometers) * fallbackIndex;
        var pathDelay = EnumerateInternalPaths(group)
            .Sum(path => (path.Path?.TotalLengthMicrometers ?? 0)
                * (path.DispersionModel?.GroupIndexAt(wavelengthNm) ?? DefaultGroupIndex));
        return (widthDelay + pathDelay) / SpeedOfLightMicrometersPerPicosecond;
    }

    /// <summary>
    /// Total internal optical path length in micrometers, recursive across nested
    /// groups: the geometric lengths of all internal waveguide paths plus the width of
    /// every leaf child component.
    /// </summary>
    public static double InternalPathLengthMicrometers(ComponentGroup group) =>
        EnumerateInternalPaths(group).Sum(path => path.Path?.TotalLengthMicrometers ?? 0)
        + group.GetAllComponentsRecursive()
            .Where(component => component is not ComponentGroup)
            .Sum(component => component.WidthMicrometers);

    /// <summary>The first group index the recursive internal waveguides carry, else the default.</summary>
    private static double ResolveGroupIndex(ComponentGroup group, double wavelengthNm) =>
        EnumerateInternalPaths(group)
            .Select(path => path.DispersionModel?.GroupIndexAt(wavelengthNm))
            .FirstOrDefault(index => index.HasValue)
        ?? DefaultGroupIndex;

    /// <summary>Depth-first enumeration of this group's and every nested group's frozen paths.</summary>
    private static IEnumerable<FrozenWaveguidePath> EnumerateInternalPaths(ComponentGroup group)
    {
        foreach (var path in group.InternalPaths)
            yield return path;
        foreach (var child in group.ChildComponents)
            if (child is ComponentGroup childGroup)
                foreach (var path in EnumerateInternalPaths(childGroup))
                    yield return path;
    }
}

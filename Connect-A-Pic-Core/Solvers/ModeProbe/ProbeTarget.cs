using CAP_Core.Components.ComponentHelpers;

namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// What the user's probe click landed on, as far as the mode probe cares:
/// a display label, the waveguide width (when a connection provided one), and
/// the two classifications that change the probe's behaviour.
/// </summary>
/// <param name="DisplayName">Label shown in the probe panel header.</param>
/// <param name="WaveguideWidthMicrometers">
/// Width of the clicked (or attached) waveguide connection in µm; null when unknown.
/// </param>
/// <param name="IsFiberCoupler">
/// True for grating/edge couplers — the probe additionally reports fiber overlap.
/// </param>
/// <param name="IsInterferenceRegion">
/// True for MMI/multimode components — the probe shows a notice instead of a mode.
/// </param>
public sealed record ProbeTarget(
    string DisplayName,
    double? WaveguideWidthMicrometers,
    bool IsFiberCoupler,
    bool IsInterferenceRegion)
{
    /// <summary>Builds a target for a clicked waveguide connection.</summary>
    public static ProbeTarget ForConnection(double widthMicrometers, double pathLengthMicrometers) =>
        new($"Waveguide ({pathLengthMicrometers:F1} µm)", widthMicrometers,
            IsFiberCoupler: false, IsInterferenceRegion: false);

    /// <summary>
    /// Builds a target for a clicked component, classifying it as fiber coupler
    /// (grating/edge coupler, shared classifier from issue #689) or interference
    /// region (MMI etc.). <paramref name="attachedConnectionWidth"/> is the width
    /// of a waveguide attached to the component, when one exists.
    /// </summary>
    public static ProbeTarget ForComponent(string name, double? attachedConnectionWidth) =>
        new(name, attachedConnectionWidth,
            IsFiberCoupler: LightSourceClassifier.IsLightInjectingCoupler(name),
            IsInterferenceRegion: InterferenceRegionClassifier.IsInterferenceRegion(name));
}

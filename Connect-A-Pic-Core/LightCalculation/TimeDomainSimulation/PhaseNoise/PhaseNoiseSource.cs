namespace CAP_Core.LightCalculation.TimeDomainSimulation.PhaseNoise;

/// <summary>
/// Phase-noise description of one laser feeding a transient input pin (issue #834).
/// Pins sharing the same <see cref="LockGroupId"/> share ONE Wiener phase process
/// (plus their individual static offsets), so common-mode phase noise cancels in
/// balanced paths naturally; pins in different groups random-walk independently
/// and their beat carries the combined linewidth.
/// </summary>
/// <param name="LinewidthHz">Lorentzian FWHM linewidth Δν in Hz (≤ 0 = ideal, no noise).</param>
/// <param name="LockGroupId">
/// Identity of the underlying laser (or master-laser lock group). Same id = phase-locked.
/// </param>
/// <param name="PhaseOffsetRad">Static phase offset added on top of the shared walk.</param>
public sealed record PhaseNoiseSource(
    double LinewidthHz,
    string LockGroupId,
    double PhaseOffsetRad = 0);

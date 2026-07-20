namespace CAP_Core.LightCalculation;

/// <summary>
/// Optional circuit knowledge for <see cref="TransitiveSMatrixCalculator.Compute"/>
/// (field round 4, final batch). All members are optional; a null context reproduces
/// the bare mathematical behaviour (solve every column, guard every transfer).
/// </summary>
public sealed record TransitiveClosureContext
{
    /// <summary>
    /// Maps each pin flow id to the display name of the component that owns it. Enables
    /// (a) the single-hop passivity pre-check per component block and (b) naming the
    /// components of a feedback loop when the solve is singular.
    /// </summary>
    public IReadOnlyDictionary<Guid, string>? PinOwnerNames { get; init; }

    /// <summary>
    /// Pins that are externally observable (circuit ports, e.g. coupler pins or a
    /// group's external pins). The energy guard (|H| ≤ 1) applies only to transfers
    /// between two observable pins: field enhancement at pins INSIDE a resonator
    /// legitimately exceeds 1 (cavity buildup) and must not be rejected. Null guards
    /// every pair (safe for flat feed-forward circuits and unit tests).
    /// </summary>
    public IReadOnlyCollection<Guid>? ExternallyObservablePinIds { get; init; }

    /// <summary>
    /// Pins whose closure columns are needed (the active light sources). Null solves
    /// every column (full closure). Restricting the columns keeps the solve at one
    /// O(n²) substitution per source after the single O(n³) factorization.
    /// </summary>
    public IReadOnlyCollection<Guid>? SourcePinIds { get; init; }

    /// <summary>Wavelength the closure is computed for, named in error messages.</summary>
    public int? WavelengthNm { get; init; }
}

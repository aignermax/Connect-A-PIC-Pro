namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Session store for the last manually entered mode-solver cross-section.
/// The ModeSolverDialog writes here on every solve; the mode probe reads it as
/// the fallback when the active PDK does not carry geometry/material data
/// (the probe then shows a "geometry assumed" hint).
/// </summary>
public class CrossSectionDefaultsStore
{
    /// <summary>The last manually entered cross-section; starts at the built-in SOI defaults.</summary>
    public ProbeCrossSection LastManualCrossSection { get; private set; } = ProbeCrossSection.Default;

    /// <summary>Records the cross-section the user last entered manually.</summary>
    public void RecordManualEntry(
        double widthMicrometers,
        double heightMicrometers,
        double slabHeightMicrometers,
        double coreIndex,
        double cladIndex)
    {
        LastManualCrossSection = new ProbeCrossSection(
            widthMicrometers, heightMicrometers, slabHeightMicrometers,
            coreIndex, cladIndex,
            IsGeometryAssumed: true,
            SourceDescription: "last manual dialog entry");
    }
}

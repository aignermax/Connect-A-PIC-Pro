using CAP_Core.Components.Process;

namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Resolves the waveguide cross-section for a probe click from the authoritative
/// sources — the clicked connection's width and the active PDK process fingerprint
/// (core thickness, core/cladding materials) — falling back to the last manually
/// entered ModeSolverDialog values when the PDK does not carry the data.
/// </summary>
public static class ProbeCrossSectionResolver
{
    private const double NanometersPerMicrometer = 1000.0;

    /// <summary>
    /// Builds a <see cref="ProbeCrossSection"/> from the given sources.
    /// </summary>
    /// <param name="connectionWidthMicrometers">
    /// Width of the clicked waveguide connection in µm, or null when the probe hit a
    /// component and no attached connection provides a width.
    /// </param>
    /// <param name="process">Active PDK process fingerprint, or null when none is set.</param>
    /// <param name="fallback">
    /// Fallback values (typically the last manually entered ModeSolverDialog cross-section).
    /// </param>
    public static ProbeCrossSection Resolve(
        double? connectionWidthMicrometers,
        ProcessFingerprint? process,
        ProbeCrossSection fallback)
    {
        var sources = new List<string>();
        bool assumed = false;

        double width = ResolveWidth(connectionWidthMicrometers, fallback, sources, ref assumed);
        double height = ResolveHeight(process, fallback, sources, ref assumed);
        var (coreIndex, cladIndex) = ResolveIndices(process, fallback, sources, ref assumed);

        return new ProbeCrossSection(
            WidthMicrometers: width,
            HeightMicrometers: height,
            SlabHeightMicrometers: fallback.SlabHeightMicrometers,
            CoreIndex: coreIndex,
            CladIndex: cladIndex,
            IsGeometryAssumed: assumed,
            SourceDescription: string.Join(", ", sources));
    }

    private static double ResolveWidth(
        double? connectionWidth, ProbeCrossSection fallback, List<string> sources, ref bool assumed)
    {
        if (connectionWidth is > 0)
        {
            sources.Add("width from connection");
            return connectionWidth.Value;
        }
        assumed = true;
        sources.Add("width assumed");
        return fallback.WidthMicrometers;
    }

    private static double ResolveHeight(
        ProcessFingerprint? process, ProbeCrossSection fallback, List<string> sources, ref bool assumed)
    {
        if (process?.CoreThicknessNm is > 0)
        {
            sources.Add("thickness from PDK process");
            return process.CoreThicknessNm.Value / NanometersPerMicrometer;
        }
        assumed = true;
        sources.Add("thickness assumed");
        return fallback.HeightMicrometers;
    }

    private static (double core, double clad) ResolveIndices(
        ProcessFingerprint? process, ProbeCrossSection fallback, List<string> sources, ref bool assumed)
    {
        bool coreKnown = MaterialIndexCatalog.TryGetIndex(process?.CoreMaterial, out var core);
        bool cladKnown = MaterialIndexCatalog.TryGetIndex(process?.Cladding, out var clad);

        if (coreKnown && cladKnown)
        {
            sources.Add($"indices from PDK materials ({process!.CoreMaterial}/{process.Cladding})");
            return (core, clad);
        }

        assumed = true;
        sources.Add("indices assumed");
        return (coreKnown ? core : fallback.CoreIndex, cladKnown ? clad : fallback.CladIndex);
    }
}

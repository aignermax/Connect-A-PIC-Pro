namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Maps Lunima's SiEPIC <c>nazcaFunction</c> names to ubcpdk cell names (the SiEPIC
/// EBeam PDK for gdsfactory). Verified against a real install (gdsfactory 9.34.2 +
/// ubcpdk 3.3.4): 35 names exist verbatim in ubcpdk's <c>PDK.cells</c> registry,
/// three differ only in spelling, four have no equivalent (callers fall back to
/// stub geometry). See docs/superpowers/specs/2026-07-02-gdsfactory-export-design.md.
/// </summary>
public static class UbcPdkCellMap
{
    private static readonly HashSet<string> VerbatimCells = new(StringComparer.Ordinal)
    {
        "ANT_MMI_1x2_te1550_3dB_BB",
        "GC_SiN_TE_1310_8degOxide_BB",
        "GC_SiN_TE_1550_8degOxide_BB",
        "GC_TE_1310_8degOxide_BB",
        "GC_TE_1550_8degOxide_BB",
        "GC_TM_1310_8degOxide_BB",
        "GC_TM_1550_8degOxide_BB",
        "crossing_horizontal",
        "crossing_manhattan",
        "ebeam_BondPad",
        "ebeam_DC_te895",
        "ebeam_MMI_2x2_5050_te1310",
        "ebeam_Polarizer_TM_1550_UQAM",
        "ebeam_YBranch_895",
        "ebeam_YBranch_te1310",
        "ebeam_adiabatic_te1550",
        "ebeam_adiabatic_tm1550",
        "ebeam_bdc_te1550",
        "ebeam_crossing4",
        "ebeam_gc_te1310",
        "ebeam_gc_te1550",
        "ebeam_gc_te895",
        "ebeam_splitter_swg_assist_te1310",
        "ebeam_splitter_swg_assist_te1550",
        "ebeam_terminator_SiN_1550",
        "ebeam_terminator_SiN_te895",
        "ebeam_terminator_te1310",
        "ebeam_terminator_te1550",
        "ebeam_terminator_tm1550",
        "ebeam_y_1550",
        "ebeam_y_adiabatic",
        "ebeam_y_adiabatic_500pin",
        "taper_SiN_750_3000",
        "taper_si_simm_1310",
        "taper_si_simm_1550",
    };

    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["ebeam_DC_2-1_te895"] = "ebeam_DC_2m1_te895",
        ["ebeam_routing_taper_te1550_w=500nm_to_w=3000nm_L=20um"]
            = "ebeam_routing_taper_te1550_w500nm_to_w3000nm_L20um",
        ["ebeam_routing_taper_te1550_w=500nm_to_w=3000nm_L=40um"]
            = "ebeam_routing_taper_te1550_w500nm_to_w3000nm_L40um",
    };

    /// <summary>
    /// Returns the ubcpdk cell name for <paramref name="nazcaFunction"/>, or null when
    /// no equivalent exists (the caller falls back to stub geometry).
    /// </summary>
    public static string? MapToUbcPdkCell(string? nazcaFunction)
    {
        if (string.IsNullOrEmpty(nazcaFunction))
            return null;
        if (VerbatimCells.Contains(nazcaFunction))
            return nazcaFunction;
        return Renames.TryGetValue(nazcaFunction, out var renamed) ? renamed : null;
    }
}

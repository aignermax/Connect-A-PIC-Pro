using CAP.Avalonia.Services.GdsFactoryExport;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// Tests for the nazcaFunction → ubcpdk cell-name mapping. The exact-hit list and
/// the renames were verified against a real install (gdsfactory 9.34.2 + ubcpdk 3.3.4,
/// see docs/superpowers/specs/2026-07-02-gdsfactory-export-design.md).
/// </summary>
public class UbcPdkCellMapTests
{
    [Theory]
    [InlineData("ebeam_y_1550")]
    [InlineData("ebeam_bdc_te1550")]
    [InlineData("ebeam_crossing4")]
    [InlineData("ebeam_gc_te1550")]
    [InlineData("GC_TE_1550_8degOxide_BB")]
    [InlineData("ebeam_MMI_2x2_5050_te1310")]
    [InlineData("crossing_horizontal")]
    [InlineData("taper_si_simm_1550")]
    public void MapToUbcPdkCell_VerbatimUbcPdkNames_MapIdentically(string name)
    {
        UbcPdkCellMap.MapToUbcPdkCell(name).ShouldBe(name);
    }

    [Theory]
    [InlineData("ebeam_DC_2-1_te895", "ebeam_DC_2m1_te895")]
    [InlineData("ebeam_routing_taper_te1550_w=500nm_to_w=3000nm_L=20um",
                "ebeam_routing_taper_te1550_w500nm_to_w3000nm_L20um")]
    [InlineData("ebeam_routing_taper_te1550_w=500nm_to_w=3000nm_L=40um",
                "ebeam_routing_taper_te1550_w500nm_to_w3000nm_L40um")]
    public void MapToUbcPdkCell_KnownRenames_MapToUbcPdkSpelling(string nazca, string expected)
    {
        UbcPdkCellMap.MapToUbcPdkCell(nazca).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ebeam_dc_te1550")]                 // kein ubcpdk-Pendant (nur bdc)
    [InlineData("ebeam_dc_halfring_straight")]
    [InlineData("ebeam_taper_te1550")]
    [InlineData("contra_directional_coupler")]
    [InlineData("demo_pdk.ring_resonator")]
    [InlineData("")]
    [InlineData(null)]
    public void MapToUbcPdkCell_UnknownNames_ReturnNull(string? name)
    {
        UbcPdkCellMap.MapToUbcPdkCell(name).ShouldBeNull();
    }
}

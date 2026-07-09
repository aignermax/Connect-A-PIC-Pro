using CAP_Core.Export.PdkResolution;
using Shouldly;
using Xunit;

namespace UnitTests.Export.PdkResolution;

/// <summary>
/// Locks in the nazcaFunction → (module, function) mapping used by the PDK
/// consistency check (issue #515). Must stay consistent with the mapping the
/// PDK Offset Editor preview applies at click time.
/// </summary>
public class NazcaFunctionPathTests
{
    [Theory]
    // The dead-reference case from PR #511: demo_pdk is canonicalised to demo.
    [InlineData("demo_pdk.ring_resonator", "demo", "ring_resonator")]
    [InlineData("demo.mmi2x2_dp", "demo", "mmi2x2_dp")]
    [InlineData("demo.shallow.strt", "demo.shallow", "strt")]
    [InlineData("demo_pdk.shallow.strt", "demo.shallow", "strt")]
    [InlineData("other_module.sub.func", "other_module.sub", "func")]
    public void Split_DottedPaths_SplitsAtLastDot(string input, string expectedModule, string expectedFunction)
    {
        var (module, function) = NazcaFunctionPath.Split(input);
        module.ShouldBe(expectedModule);
        function.ShouldBe(expectedFunction);
    }

    [Theory]
    [InlineData("ebeam_y_1550")]
    [InlineData("gc_te1550")]
    [InlineData("GC_TE_1550_8degOxide_BB")]
    [InlineData("ANT_MMI_1x2")]
    [InlineData("crossing_horizontal")]
    [InlineData("taper_si_simm_1550")]
    [InlineData("contra_directional_coupler")]
    public void Split_FlatSiepicNames_MapToSiepicPackage(string input)
    {
        var (module, function) = NazcaFunctionPath.Split(input);
        module.ShouldBe("siepic_ebeam_pdk");
        function.ShouldBe(input);
    }

    [Fact]
    public void Split_FlatUnknownName_DefaultsToDemofab()
    {
        var (module, function) = NazcaFunctionPath.Split("strt");
        module.ShouldBe("demo");
        function.ShouldBe("strt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_EmptyInput_ReturnsDemoWithEmptyFunction(string? input)
    {
        var (module, function) = NazcaFunctionPath.Split(input);
        module.ShouldBe("demo");
        function.ShouldBe("");
    }
}

using CAP.Avalonia.Services.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Tests for <see cref="FoundryEditCodeSynthesis"/> — the single resolver that turns a bundled
/// foundry component's function reference into runnable editor code plus the backend that can
/// execute it. Field round 6: the round-4 fix only covered components carrying a
/// <c>GdsFactoryFunction</c> (cspdk); SiEPIC components reference their cells via
/// <c>nazcaModuleName</c>/<c>nazcaFunction</c>, so the synthesis kept emitting the
/// module-attribute call <c>siepic_ebeam_pdk.ebeam_adiabatic_te1550()</c>, which fails because
/// siepic_ebeam_pdk is a KLayout package, not a Nazca/gdsfactory module. SiEPIC cells are
/// instantiable via the ubcpdk gdsfactory registry instead.
/// </summary>
public class FoundryEditCodeSynthesisTests
{
    // ── cspdk regression (round-4 fix must keep working) ───────────────────

    [Fact]
    public void For_ModuleQualifiedGdsFactoryFunction_UsesThePdkRegistryPattern()
    {
        var result = FoundryEditCodeSynthesis.For(
            "cspdk.sin300.coupler_straight", null, null, null);

        result.ShouldNotBeNull();
        result.Value.Backend.ShouldBe(GeometryBackend.GdsFactory);
        result.Value.Code.ShouldBe(
            "import gdsfactory as gf\n"
            + "import cspdk.sin300\n"
            + "cspdk.sin300.PDK.activate()\n"
            + "component = gf.get_component('coupler_straight')\n");
    }

    [Fact]
    public void For_BareGdsFactoryCellName_ResolvesViaGetComponent()
    {
        var result = FoundryEditCodeSynthesis.For("straight", null, null, null);

        result.ShouldNotBeNull();
        result.Value.Backend.ShouldBe(GeometryBackend.GdsFactory);
        result.Value.Code.ShouldContain("gf.get_component('straight')");
        result.Value.Code.ShouldNotContain("import straight");
    }

    // ── SiEPIC (field round 6) ──────────────────────────────────────────────

    [Fact]
    public void For_SiepicNazcaReference_SynthesizesUbcPdkRegistryCode_notModuleAttributeCall()
    {
        // Field bug: "Edit Component" on "Adiabatic Coupler TE 1550" synthesized
        // "import siepic_ebeam_pdk ... siepic_ebeam_pdk.ebeam_adiabatic_te1550()" which fails
        // with "module 'siepic_ebeam_pdk' has no attribute 'ebeam_adiabatic_te1550'" —
        // siepic_ebeam_pdk is a KLayout package without cell attributes. The cell IS
        // instantiable via the ubcpdk gdsfactory registry (verified in the managed env).
        var result = FoundryEditCodeSynthesis.For(
            null, "siepic_ebeam_pdk", "ebeam_adiabatic_te1550", null);

        result.ShouldNotBeNull();
        result.Value.Backend.ShouldBe(GeometryBackend.GdsFactory);
        result.Value.Code.ShouldBe(
            "import gdsfactory as gf\n"
            + "import ubcpdk\n"
            + "ubcpdk.PDK.activate()\n"
            + "component = gf.get_component('ebeam_adiabatic_te1550')\n");
    }

    [Fact]
    public void For_SiepicCellWithUbcPdkRename_UsesTheRenamedRegistryName()
    {
        var result = FoundryEditCodeSynthesis.For(
            null, "siepic_ebeam_pdk", "ebeam_DC_2-1_te895", null);

        result.ShouldNotBeNull();
        result.Value.Backend.ShouldBe(GeometryBackend.GdsFactory);
        result.Value.Code.ShouldContain("gf.get_component('ebeam_DC_2m1_te895')");
    }

    [Fact]
    public void For_SiepicCellWithoutUbcPdkEquivalent_ReturnsNull_insteadOfBrokenAttributeCode()
    {
        // The four KLayout-only cells (no ubcpdk registry entry) have no runnable editor
        // code — null means "no stored code", which is honest; module-attribute code would
        // only reproduce the AttributeError.
        FoundryEditCodeSynthesis.For(null, "siepic_ebeam_pdk", "contra_directional_coupler", null)
            .ShouldBeNull();
    }

    // ── demo PDK (nazca) regression ─────────────────────────────────────────

    [Fact]
    public void For_DemoNazcaReference_SynthesizesNazcaDemofabCode()
    {
        var result = FoundryEditCodeSynthesis.For(null, null, "demo.mmi2x2_dp", null);

        result.ShouldNotBeNull();
        result.Value.Backend.ShouldBe(GeometryBackend.Nazca);
        result.Value.Code.ShouldContain("import nazca as nd");
        result.Value.Code.ShouldContain("def component():");
        result.Value.Code.ShouldContain("nazca.demofab");
        result.Value.Code.ShouldContain("mmi2x2_dp");
        result.Value.Code.ShouldNotContain("component = demo");
    }

    [Fact]
    public void For_NoReferenceAtAll_ReturnsNull()
    {
        FoundryEditCodeSynthesis.For(null, null, null, null).ShouldBeNull();
        FoundryEditCodeSynthesis.For("", "", "", "").ShouldBeNull();
    }

    // ── whole bundled SiEPIC PDK: never emit the broken attribute pattern ───

    [Fact]
    public void For_EveryBundledSiepicComponent_NeverSynthesizesTheSiepicAttributeCall()
    {
        var pdkPath = FindBundledPdk("siepic-ebeam-pdk.json");
        pdkPath.ShouldNotBeNull("bundled siepic-ebeam-pdk.json must exist in CAP-DataAccess/PDKs");
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkPath!);

        foreach (var comp in pdk.Components.Where(c => !string.IsNullOrWhiteSpace(c.NazcaFunction)))
        {
            var result = FoundryEditCodeSynthesis.For(
                comp.GdsFactoryFunction, pdk.NazcaModuleName, comp.NazcaFunction, comp.NazcaParameters);

            if (result is null)
                continue; // KLayout-only cell: no runnable editor code, honest empty editor

            result.Value.Backend.ShouldBe(GeometryBackend.GdsFactory,
                $"'{comp.Name}' must synthesize gdsfactory registry code");
            result.Value.Code.ShouldNotContain("siepic_ebeam_pdk",
                customMessage: $"'{comp.Name}' must not reference the KLayout-only siepic module");
            result.Value.Code.ShouldContain("gf.get_component(");
        }
    }

    private static string? FindBundledPdk(string fileName)
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(FoundryEditCodeSynthesisTests).Assembly.Location)!);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "CAP-DataAccess", "PDKs", fileName);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}

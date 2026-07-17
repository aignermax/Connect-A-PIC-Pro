using CAP.Avalonia.Services;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings.InstanceOverride;

/// <summary>
/// Tests for <see cref="NazcaCodeTemplateBuilder"/> (issue #556). The deterministic
/// unit tests pin the module/function resolution that broke the seeded editor code
/// (a dotted function name like "demo.mmi2x2_dp" must resolve to
/// <c>nazca.demofab.mmi2x2_dp</c>, NOT <c>nazca.demofab.demo.mmi2x2_dp</c>). The
/// integration test runs the generated code through the real preview script to prove
/// the seeded template actually renders — the end-to-end check that was missing.
/// </summary>
[Trait("Category", "Slow")]
public class NazcaCodeTemplateBuilderTests
{
    [Fact]
    public void Build_DottedFunctionName_ResolvesToDemofabLeaf_NotDemoAttribute()
    {
        // The Directional Coupler ships as nazcaFunction "demo.mmi2x2_dp".
        var code = NazcaCodeTemplateBuilder.Build(null, "demo.mmi2x2_dp", null);

        code.ShouldContain("import nazca.demofab");
        code.ShouldContain("return nazca.demofab.mmi2x2_dp()");
        code.ShouldNotContain("nazca.demofab.demo");   // the bug
        code.ShouldContain("def component():");
    }

    [Fact]
    public void Build_PlainFunctionWithModuleAndParams_EmitsCall()
    {
        var code = NazcaCodeTemplateBuilder.Build("demo", "strt", "length=20");

        code.ShouldContain("return nazca.demofab.strt(length=20)");
    }

    [Fact]
    public void Build_DemoSubPath_WalksAttributeChain()
    {
        var code = NazcaCodeTemplateBuilder.Build("demo.shallow", "wg", null);

        code.ShouldContain("return nazca.demofab.shallow.wg()");
    }

    [Fact]
    public void Build_NonDemoModule_ImportsAndCallsThatModule()
    {
        var code = NazcaCodeTemplateBuilder.Build("mypdk.cells", "ring", "radius=10");

        code.ShouldContain("import mypdk.cells");
        code.ShouldContain("return mypdk.cells.ring(radius=10)");
    }

    [Fact]
    public void Build_FunctionNameWithInvalidIdentifierChars_UsesGetattr()
    {
        // SiEPIC names like "ebeam_DC_2-1_te895" are not valid Python identifiers
        // (hyphen); "module.name" would be a syntax error, so getattr is used.
        var code = NazcaCodeTemplateBuilder.Build("siepic_ebeam_pdk", "ebeam_DC_2-1_te895", null);

        code.ShouldContain("getattr(siepic_ebeam_pdk, \"ebeam_DC_2-1_te895\")()");
        code.ShouldNotContain(".ebeam_DC_2-1_te895(");   // the invalid attribute form
    }

    // ---------------------------------------------------------------------------
    // Integration: the seeded template must ACTUALLY render through the preview
    // script. Uses PythonDiscoveryService so it finds the real nazca interpreter
    // (the bare "python3" probe used elsewhere is a Store-alias stub on Windows).
    // Skips cleanly when no nazca-capable Python is available.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GeneratedTemplate_ForDirectionalCoupler_RendersSuccessfully()
    {
        var python = await new PythonDiscoveryService().FindFirstNazcaPythonPathAsync();
        if (python == null) return;              // no nazca-capable python — env skip
        var script = FindRealPreviewScript();
        if (script == null) return;              // script not found — skip

        // The exact case that failed in the dialog: Directional Coupler = "demo.mmi2x2_dp".
        var code = NazcaCodeTemplateBuilder.Build(null, "demo.mmi2x2_dp", null);
        var svc = new NazcaComponentPreviewService(python, script);

        var result = await svc.RenderRawCodeAsync(code);

        result.Success.ShouldBeTrue(
            $"the seeded template for the Directional Coupler must render. Error: {result.Error}");
        result.XMax.ShouldBeGreaterThan(result.XMin);
    }

    private static string? FindRealPreviewScript() =>
        UnitTests.Integration.GdsAlignmentTestSetup.FindRealPreviewScript();
}

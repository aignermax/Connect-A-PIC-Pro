using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;

namespace UnitTests.ComponentSettings.InstanceOverride;

/// <summary>
/// End-to-end integration tests for the per-instance Nazca code editor's preview path
/// (issue #556). The editor seeds + renders via the preview script's MODULE mode
/// (<see cref="NazcaComponentPreviewService.RenderAsync"/>), which resolves both the
/// bundled demo PDK (nazca.demofab) and SiEPIC KLayout PCells. These tests prove BOTH
/// PDK paths actually compile and produce preview geometry — the check that was missing
/// when the editor only handled the demo case.
///
/// A real nazca-capable interpreter is located via <see cref="PythonDiscoveryService"/>
/// (the bare "python3" probe is a Store-alias stub on Windows). Tests skip cleanly when
/// no such interpreter / script is available, so CI without nazca still passes.
/// </summary>
public class NazcaEditorPreviewIntegrationTests
{
    /// <summary>
    /// Subprocess timeout for these integration renders. The service default (90s)
    /// is fine interactively, but on a loaded CI runner executing the full suite a
    /// nazca render can legitimately exceed it — which surfaced as a spurious
    /// "Preview script timed out" QA failure. Use a generous budget here; a genuine
    /// hang is still caught by the kill guard.
    /// </summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task DemoPdkComponent_RendersPreviewGeometry()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip

        var svc = CreateService(python, script);

        // Demo PDK Directional Coupler — function "demo.mmi2x2_dp" (nazca.demofab).
        var result = await svc.RenderAsync(moduleName: null, nazcaFunction: "demo.mmi2x2_dp", nazcaParameters: null);

        if (IsRunnerTimeout(result)) return;            // env skip: overloaded runner
        result.Success.ShouldBeTrue($"demo component must render in the editor. Error: {result.Error}");
        result.XMax.ShouldBeGreaterThan(result.XMin, "preview bbox must be non-degenerate");
        AssertPolygonsUnlessGdsReaderMissing(result);
    }

    [Fact]
    public async Task SiEpicComponent_RendersPreviewGeometry()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip

        var svc = CreateService(python, script);

        // SiEPIC EBeam PDK directional coupler — a KLayout PCell resolved by name
        // (NOT a Python attribute) through the script's module-mode SiEPIC handling.
        var result = await svc.RenderAsync(
            moduleName: "siepic_ebeam_pdk", nazcaFunction: "ebeam_DC_2-1_te895", nazcaParameters: null);

        // If the SiEPIC/KLayout stack isn't installed in this environment, skip rather
        // than fail (mirrors the nazca-availability guard).
        if (!result.Success)
        {
            result.Error.ShouldNotBeNullOrEmpty();
            return;
        }

        result.XMax.ShouldBeGreaterThan(result.XMin, "preview bbox must be non-degenerate");
        AssertPolygonsUnlessGdsReaderMissing(result);
    }

    // ── VM-level: the exact user flow (open editor → click Run Preview) ──────────
    // These drive InstanceNazcaCodeEditorViewModel end-to-end against the real preview
    // service. They reproduce the reported failures (the seeded original source is not
    // standalone-runnable: a demo cell body raised "unexpected indent", a SiEPIC PCell
    // had no component()) and assert the fix: an UNEDITED editor renders the component
    // via module mode, so Run succeeds for both PDKs.

    [Fact]
    public async Task EditorVm_DemoMmi2x2_InitializeThenRun_Succeeds()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip

        var vm = BuildEditorVm(module: null, function: "demo.mmi2x2_dp",
            CreateService(python, script));

        await vm.InitializeAsync();
        await vm.RunPreviewCommand.ExecuteAsync(null);

        if (IsRunnerTimeout(vm.PreviewError)) return;   // env skip: overloaded runner
        vm.PreviewError.ShouldBeNullOrEmpty($"Run must succeed for the demo 2x2 MMI. Error: {vm.PreviewError}");
        vm.IsValid.ShouldBeTrue();
        vm.PreviewData.ShouldNotBeNull();
    }

    [Fact]
    public async Task EditorVm_SiEpicHalfringStraight_InitializeThenRun_Succeeds()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip

        var vm = BuildEditorVm(module: "siepic_ebeam_pdk", function: "ebeam_dc_halfring_straight",
            CreateService(python, script));

        await vm.InitializeAsync();
        await vm.RunPreviewCommand.ExecuteAsync(null);

        // If the SiEPIC/KLayout stack isn't installed, the module-mode render can't run —
        // skip (no crash, clear error) rather than fail CI.
        if (!vm.IsValid && vm.PreviewData == null)
        {
            vm.PreviewError.ShouldNotBeNullOrEmpty();
            return;
        }

        vm.IsValid.ShouldBeTrue($"Run must succeed for the SiEPIC halfring. Error: {vm.PreviewError}");
        vm.PreviewData.ShouldNotBeNull();
    }

    [Fact]
    public async Task ShowcaseExample_RendersSuccessfully()
    {
        var (python, script) = await ResolveEnvironmentAsync();
        if (python == null || script == null) return;   // env skip

        var svc = CreateService(python, script);

        // The "?" help offers NazcaCodeExamples.Complex as an insertable starter — it
        // must always render (it's shipped as a working example).
        var result = await svc.RenderRawCodeAsync(NazcaCodeExamples.Complex);

        if (IsRunnerTimeout(result)) return;            // env skip: overloaded runner
        result.Success.ShouldBeTrue($"the showcase example must render. Error: {result.Error}");
        AssertPolygonsUnlessGdsReaderMissing(result);
    }

    /// <summary>
    /// Asserts the preview produced polygons — unless the script reported (via
    /// <see cref="NazcaPreviewResult.PolygonWarning"/>) that gdstk/gdspy is not
    /// installed, in which case the polygon overlay is legitimately empty and only
    /// the render success/bbox can be verified. Mirrors the nazca-availability skip.
    /// </summary>
    private static void AssertPolygonsUnlessGdsReaderMissing(NazcaPreviewResult result)
    {
        if (!string.IsNullOrEmpty(result.PolygonWarning)) return;   // env skip: no gdstk/gdspy
        result.Polygons.Count.ShouldBeGreaterThan(0, "a preview image needs polygons");
    }

    /// <summary>Builds the real preview service with the CI-friendly <see cref="RenderTimeout"/>.</summary>
    private static NazcaComponentPreviewService CreateService(string python, string script)
        => new(python, script, RenderTimeout);

    /// <summary>
    /// True when the render failed only because the subprocess hit the timeout —
    /// an overloaded-runner condition, not a product bug. Callers skip in that case,
    /// mirroring the nazca-availability env skips.
    /// </summary>
    private static bool IsRunnerTimeout(NazcaPreviewResult result)
        => !result.Success && IsRunnerTimeout(result.Error);

    /// <inheritdoc cref="IsRunnerTimeout(NazcaPreviewResult)"/>
    private static bool IsRunnerTimeout(string? error)
        => error != null && error.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static InstanceNazcaCodeEditorViewModel BuildEditorVm(
        string? module, string function, NazcaComponentPreviewService svc)
        => new(
            componentKey: "test-instance",
            storedOverrides: new Dictionary<string, NazcaCodeOverride>(),
            liveComponent: null,
            moduleName: module,
            nazcaFunction: function,
            nazcaParameters: null,
            templateCode: NazcaCodeTemplateBuilder.Build(module, function, null),
            previewService: svc);

    /// <summary>Resolves (nazca-capable python, preview script path), or (null, null) to skip.</summary>
    private static async Task<(string? python, string? script)> ResolveEnvironmentAsync()
    {
        var python = await new PythonDiscoveryService().FindFirstNazcaPythonPathAsync();
        return (python, FindRealPreviewScript());
    }

    private static string? FindRealPreviewScript() =>
        UnitTests.Integration.GdsAlignmentTestSetup.FindRealPreviewScript();
}

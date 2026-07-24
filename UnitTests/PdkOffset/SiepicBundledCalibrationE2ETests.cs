using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using UnitTests.Integration;

namespace UnitTests.PdkOffset;

/// <summary>
/// End-to-end calibration lock for the bundled SiEPIC EBeam PDK JSON against
/// the REAL preview pipeline (managed python + render_component_preview.py +
/// siepic_ebeam_pdk). Round-5 field report: DC Halfring-Straight (Δmax 9.96 µm),
/// Contra-Directional Coupler (Δmax 4.46 µm) and both Adiabatic Couplers
/// (Δ 0.30 µm) shipped with <c>nazcaOriginOffsetY</c> written as -YMin instead
/// of YMax (the #635 convention bug), which shifts the placed cell against the
/// Lunima pin grid in every GDS export. The JSONs are fixed; these tests fail
/// the moment the data (or the pipeline math) drifts again.
///
/// Opt-in like the other env-dependent tests: runs only when
/// LUNIMA_TEST_PYTHON3 points at a nazca-capable interpreter; skips otherwise.
/// </summary>
[Trait("Category", "Slow")]
public class SiepicBundledCalibrationE2ETests
{
    private static PdkComponentDraft? LoadBundledDraft(string componentName)
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SiepicBundledCalibrationE2ETests).Assembly.Location)!);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "CAP-DataAccess", "PDKs", "siepic-ebeam-pdk.json");
            if (File.Exists(candidate))
            {
                var pdk = new PdkLoader().LoadFromFileForEditing(candidate);
                return pdk.Components.FirstOrDefault(c =>
                    string.Equals(c.Name, componentName, StringComparison.Ordinal));
            }
            current = current.Parent;
        }
        return null;
    }

    private static async Task<NazcaPreviewResult?> RenderOrSkip(PdkComponentDraft draft)
    {
        var (python, script) = await GdsAlignmentTestSetup.ResolveEnvironmentAsync();
        if (python == null || script == null) return null;   // env skip

        var svc = new NazcaComponentPreviewService(python, script);
        var (module, function) = CAP.Avalonia.ViewModels.PdkOffset
            .PdkOffsetEditorViewModel.ResolveModuleAndFunction(draft.NazcaFunction);
        var result = await svc.RenderAsync(module, function, draft.NazcaParameters);
        // siepic_ebeam_pdk / klayout not installed → env skip, not a Lunima bug.
        if (!result.Success && (result.Error?.Contains("not installed") == true
                                || result.Error?.Contains("No module named") == true
                                || result.Error?.Contains("requires klayout") == true))
            return null;
        return result;
    }

    /// <summary>
    /// The four components whose pin offsets were corrected from the real
    /// Auto-Calibrate computation must evaluate as Aligned within the strict
    /// 0.1 µm band under the same math the editor's Check-All uses.
    /// </summary>
    [Theory]
    [InlineData("DC Halfring-Straight")]
    [InlineData("Contra-Directional Coupler")]
    [InlineData("Adiabatic Coupler TE 1550")]
    [InlineData("Adiabatic Coupler TM 1550")]
    public async Task FixedBundledComponents_AreAlignedWithinStrictTolerance(string componentName)
    {
        var draft = LoadBundledDraft(componentName);
        draft.ShouldNotBeNull($"'{componentName}' must exist in the bundled SiEPIC JSON");

        var result = await RenderOrSkip(draft!);
        if (result == null) return;   // env skip

        result.Success.ShouldBeTrue($"render failed: {result.Error}");
        var check = PdkOffsetCalibration.Evaluate(
            draft!, result,
            PdkOffsetCalibration.AlignedToleranceMicrometers,
            PdkOffsetCalibration.CheckToleranceMicrometers);

        check.Status.ShouldBe(ComponentCheckStatus.Aligned,
            $"{componentName}: Δmax {check.WorstDeltaMicrometers:F4} µm — {check.Message}");
        check.WorstDeltaMicrometers.ShouldBeLessThanOrEqualTo(
            PdkOffsetCalibration.AlignedToleranceMicrometers);
    }

    /// <summary>
    /// The adiabatic couplers keep their geometry in a sub-cell; the preview
    /// must extract it recursively (round-5 fix). Zero polygons here means the
    /// silent-empty-render bug is back.
    /// </summary>
    [Theory]
    [InlineData("Adiabatic Coupler TE 1550")]
    [InlineData("Adiabatic Coupler TM 1550")]
    public async Task AdiabaticCouplers_RenderSubCellGeometry(string componentName)
    {
        var draft = LoadBundledDraft(componentName);
        draft.ShouldNotBeNull();

        var result = await RenderOrSkip(draft!);
        if (result == null) return;   // env skip

        result.Success.ShouldBeTrue($"render failed: {result.Error}");
        result.Polygons.Count.ShouldBeGreaterThan(0,
            "sub-cell geometry must be extracted recursively (silent dashed-box bug)");
    }

    /// <summary>
    /// Documented ground truth: ebeam_BondPad exposes NO optical pins — its four
    /// pins (m_pin_top/bottom/left/right) are electrical, on PinRecM layer 1/11,
    /// which the optical calibration deliberately does not read. The report
    /// status "NoNazcaPins" is therefore correct, not a data bug.
    /// </summary>
    [Fact]
    public async Task BondPad_ReportsNoOpticalPins_ByDesign()
    {
        var draft = LoadBundledDraft("Bond Pad");
        draft.ShouldNotBeNull();

        var result = await RenderOrSkip(draft!);
        if (result == null) return;   // env skip

        result.Success.ShouldBeTrue($"render failed: {result.Error}");
        var check = PdkOffsetCalibration.Evaluate(
            draft!, result,
            PdkOffsetCalibration.AlignedToleranceMicrometers,
            PdkOffsetCalibration.CheckToleranceMicrometers);
        check.Status.ShouldBe(ComponentCheckStatus.NoNazcaPins);
    }
}

using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Regression tests for issue #635: the demo 90° Bend (demo.shallow.bend) was
/// exported ~190.6 µm too high because Auto-Calibrate stored the cell org's
/// distance to the bbox BOTTOM edge (-YMin) while the export mapper
/// (<see cref="NazcaCoordinateMapper"/>) interprets NazcaOriginOffsetY as the
/// distance to the bbox TOP edge (YMax). The two only coincide for Y-symmetric
/// cells — every demofab cell except the bend (bbox Y ∈ [-9.4, 200], org = a0
/// at (0,0)), where the mismatch is YMax + YMin = 190.6 µm.
/// </summary>
public class PdkOffsetCalibrationConventionTests
{
    private const double Tolerance = 0.001;

    // Real demofab demo.shallow.bend(angle=90) geometry, engine-verified:
    // bbox (0, -9.4, 209.4, 200); a0 at (0, 0, 180°); b0 at (200, 200, 90°).
    private const double BendXMin = 0;
    private const double BendYMin = -9.400000000000006;
    private const double BendXMax = 209.4;
    private const double BendYMax = 200;

    private static NazcaPreviewResult RealBendRender() => new()
    {
        Success = true,
        XMin = BendXMin, YMin = BendYMin, XMax = BendXMax, YMax = BendYMax,
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "a0", X = 0,   Y = 0,   Angle = 180 },
            new() { Name = "b0", X = 200, Y = 200, Angle = 90 },
        }
    };

    private static PdkComponentDraft BendDraft() => new()
    {
        Name = "90° Bend",
        NazcaFunction = "demo.shallow.bend",
        NazcaParameters = "angle=90",
        WidthMicrometers = 209.4,
        HeightMicrometers = 209.4,
        NazcaOriginOffsetX = 0,
        NazcaOriginOffsetY = 200,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "a0", OffsetXMicrometers = 0,   OffsetYMicrometers = 200, AngleDegrees = 180 },
            new() { Name = "b0", OffsetXMicrometers = 200, OffsetYMicrometers = 0,   AngleDegrees = 270 },
        }
    };

    [Fact]
    public void ApplyAutoCalibrate_AsymmetricBendBbox_WritesTopEdgeOriginOffset()
    {
        var draft = BendDraft();
        // Simulate the stale pre-#635 calibration as the starting point.
        draft.NazcaOriginOffsetY = 9.400000000000006;

        var outcome = PdkOffsetCalibration.ApplyAutoCalibrate(draft, RealBendRender());

        outcome.ShouldBe(AutoCalibrateOutcome.Success);
        draft.WidthMicrometers.ShouldBe(209.4, Tolerance);
        draft.HeightMicrometers.ShouldBe(209.4, Tolerance);
        draft.NazcaOriginOffsetX!.Value.ShouldBe(0, Tolerance);
        // The mapper's contract: offset = org measured from the bbox TOP edge
        // (YMax), NOT the bottom edge (-YMin = 9.4) the old code stored.
        draft.NazcaOriginOffsetY!.Value.ShouldBe(200, Tolerance);

        var a0 = draft.Pins.Single(p => p.Name == "a0");
        var b0 = draft.Pins.Single(p => p.Name == "b0");
        a0.OffsetXMicrometers.ShouldBe(0, Tolerance);
        a0.OffsetYMicrometers.ShouldBe(200, Tolerance);   // YMax - 0
        b0.OffsetXMicrometers.ShouldBe(200, Tolerance);
        b0.OffsetYMicrometers.ShouldBe(0, Tolerance);     // YMax - 200
    }

    [Fact]
    public void Evaluate_CorrectlyCalibratedBend_ReportsAligned()
    {
        // With the corrected calibration the Check-All verdict must be Aligned;
        // under the old (H - OffsetY) - oy projection it would report the bend
        // ~190.6 µm off and Try-Fix-All would "fix" it back to the broken state.
        var check = PdkOffsetCalibration.Evaluate(BendDraft(), RealBendRender(), 0.5);

        check.Status.ShouldBe(ComponentCheckStatus.Aligned, check.Message);
        check.WorstDeltaMicrometers.ShouldBeLessThan(Tolerance);
    }

    [Fact]
    public void GetCellPlacement_CalibratedBend_PutsOrgOnRealA0Position()
    {
        // The real bend cell's org (= a0) sits at its cell origin; with the
        // corrected offset the mapper must put the org 200 µm below the box
        // top so the geometry lands on the canvas rectangle. The pre-#635
        // offset (9.4) put it at -409.4 → geometry 190.6 µm too high.
        var comp = CreateBendComponent(x: 100, y: 400);

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(100, Tolerance);
        placement.Y.ShouldBe(-600, Tolerance);
        placement.RotationDegrees.ShouldBe(0, Tolerance);

        // Org + real cell-local a0 (0,0) must coincide with the app pin's
        // Nazca position — the #565 pin-coincidence contract.
        var a0 = comp.PhysicalPins.Single(p => p.Name == "a0");
        var (pinX, pinY) = NazcaCoordinateMapper.GetPinNazcaPosition(a0);
        pinX.ShouldBe(placement.X, Tolerance);
        pinY.ShouldBe(placement.Y, Tolerance);
    }

    [Fact]
    public void DemoPdkJson_ShallowBend_ShipsTopEdgeOriginOffset()
    {
        // Pins the shipped calibration data itself: demo.shallow.bend's org is
        // 200 µm below the bbox top edge (engine-verified), so the JSON must
        // store 200 — the stale 9.4 (= -YMin) reproduces the #635 offset.
        var template = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .FirstOrDefault(t => t.NazcaFunctionName == "demo.shallow.bend");
        if (template == null) return;   // PDKs not copied to output — env skip

        template.NazcaOriginOffsetX.ShouldBe(0, Tolerance);
        template.NazcaOriginOffsetY.ShouldBe(200, Tolerance);
    }

    /// <summary>Builds a live component matching the corrected 90° Bend template.</summary>
    private static Component CreateBendComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var comp = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "demo.shallow.bend",
            nazcaFunctionParams: "angle=90",
            parts: parts,
            typeNumber: 0,
            identifier: "bend_dut",
            rotationCounterClock: DiscreteRotation.R0);
        comp.WidthMicrometers = 209.4;
        comp.HeightMicrometers = 209.4;
        comp.NazcaOriginOffsetX = 0;
        comp.NazcaOriginOffsetY = 200;
        comp.PhysicalX = x;
        comp.PhysicalY = y;
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 200,
            AngleDegrees = 180, ParentComponent = comp
        });
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "b0", OffsetXMicrometers = 200, OffsetYMicrometers = 0,
            AngleDegrees = 270, ParentComponent = comp
        });
        return comp;
    }
}

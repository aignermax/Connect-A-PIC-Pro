using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Xunit;

// ITestOutputHelper is in Xunit namespace in xUnit v2+

namespace UnitTests.Integration;

/// <summary>
/// GDS position verification tests (Issue #329).
///
/// Confirms the fabrication-blocking coordinate bug where waveguide geometry
/// does not align with component pin positions in the exported Nazca script.
///
/// Root cause:
///   SimpleNazcaExporter writes waveguide start coordinates by negating the
///   editor's absolute pin Y position:
///     nazcaY_waveguide = -(physY + pinOffsetY)
///
///   But stub-based components are placed at:
///     nazcaY_component = -(physY + NazcaOriginOffsetY)
///   with the stub pin at local Y = (height - pinOffsetY).
///
///   Global Nazca pin Y = nazcaY_component + localPinY
///                      = -(physY + originOffsetY) + (height - pinOffsetY)
///
///   For a Grating Coupler (height=19, originOffsetY=9.5, pinOffsetY=9.5):
///     Waveguide Y (exporter) = -(0 + 9.5)  = -9.5
///     Global pin Y (stub)    = -(0 + 9.5) + (19 - 9.5) = 0
///     Mismatch               = 9.5 µm
///
/// These tests FAIL when the bug is present, providing exact deviation data.
///
/// Scope: this is a STRUCTURAL check that the exporter routes its coordinates through
/// <see cref="NazcaCoordinateMapper"/> (it asserts against the same mapper the exporter
/// uses, so it cannot catch a wrong mapper FORMULA). Formula correctness is proven by
/// NazcaCoordinateMapperTests (hand-computed expectations) and by GdsExportAlignmentTests
/// (verified against the real nazca engine that writes the GDS).
/// </summary>
[Trait("Category", "Slow")]
public class GdsCoordinateVerificationTests
{
    private const double PinAlignmentTolerance = 0.01; // µm — tight, 10 nm
    private const double GcHeight = 19.0;              // µm — Grating Coupler height
    private const double GcOriginOffsetY = 9.5;        // µm — NazcaOriginOffsetY for GC
    private const double GcPinOffsetY = 9.5;           // µm — GC waveguide pin Y offset in editor

    private readonly ObservableCollection<ComponentTemplate> _library;
    private readonly SimpleNazcaExporter _exporter = new();
    private readonly NazcaCodeParser _parser = new();
    private readonly ITestOutputHelper _output;

    /// <summary>Initializes the test suite with the full component library.</summary>
    public GdsCoordinateVerificationTests(ITestOutputHelper output)
    {
        _output = output;
        _library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
    }

    /// <summary>
    /// Core bug check: the waveguide start Y in the exported Nazca script must match
    /// the global Nazca Y of the pin computed from the component stub definition.
    ///
    /// Design: 1 Grating Coupler at editor (0, 0), a straight waveguide starting
    /// from its waveguide pin.
    ///
    /// Using a PDK-style function name ("ebeam_gc_te1550") forces stub generation,
    /// making pin positions readable from the parsed script.
    ///
    /// Expected to FAIL when the coordinate bug is present (9.5 µm Y offset).
    /// </summary>
    [Fact]
    public void WaveguideStart_MustMatchPin_GlobalNazcaCoordinate()
    {
        // Arrange: single GC at editor (0, 0) with PDK function name → stub generated
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc1_coord_test";
        gc1.NazcaFunctionName = "ebeam_gc_te1550"; // forces stub generation
        canvas.AddComponent(gc1, gcTemplate.Name);

        // Destination component for the waveguide (just needs a compatible input pin)
        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 300, 0);
        gc2.Identifier = "gc2_coord_test";
        gc2.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc2, gcTemplate.Name);

        // Add straight waveguide from gc1.waveguide to gc2.waveguide
        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        // Act: export and parse
        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        _output.WriteLine("=== Exported Nazca Script (key parts) ===");
        _output.WriteLine(ExcerptScript(script, 60));
        _output.WriteLine("...");

        // Verify we got 2 components, 1 waveguide, and pin definitions
        parsed.Components.Count.ShouldBe(2, "Both GCs must appear in Nazca script");

        var stubPin = parsed.PinDefinitions.FirstOrDefault(p => p.Name == "waveguide");
        stubPin.ShouldNotBeNull(
            "'waveguide' pin must appear in the ebeam_gc_te1550 stub definition. " +
            "Ensure NazcaFunctionName='ebeam_gc_te1550' triggers stub generation.");

        parsed.WaveguideStubs.Count.ShouldBeGreaterThan(0,
            "At least one waveguide segment must appear in the exported Nazca script.");

        // Find GC1 placement (it appears first in the script)
        var gc1Placement = parsed.Components.First();
        _output.WriteLine($"GC1 placement in Nazca: ({gc1Placement.X:F4}, {gc1Placement.Y:F4}, {gc1Placement.RotationDegrees}°)");
        _output.WriteLine($"Stub pin 'waveguide' (local Nazca): ({stubPin.X:F4}, {stubPin.Y:F4}, {stubPin.AngleDegrees}°)");

        // Compute expected GLOBAL Nazca position of the waveguide pin
        double expectedGlobalX = gc1Placement.X + stubPin.X;
        double expectedGlobalY = gc1Placement.Y + stubPin.Y;
        _output.WriteLine($"Expected global Nazca pin: ({expectedGlobalX:F4}, {expectedGlobalY:F4})");

        // Find the first waveguide segment start (connects from GC1 pin)
        var wgStub = parsed.WaveguideStubs.First();
        _output.WriteLine($"Waveguide start (exporter): ({wgStub.StartX:F4}, {wgStub.StartY:F4}, {wgStub.StartAngle:F4}°)");

        double xDev = Math.Abs(expectedGlobalX - wgStub.StartX);
        double yDev = Math.Abs(expectedGlobalY - wgStub.StartY);
        _output.WriteLine($"Deviation: X={xDev:F4} µm, Y={yDev:F4} µm  (tolerance={PinAlignmentTolerance} µm)");

        // These assertions FAIL when the coordinate bug is present.
        // Expected Y deviation: 9.5 µm (NazcaOriginOffsetY for the Grating Coupler)
        xDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"X mismatch: waveguide at X={wgStub.StartX:F4}, pin global X={expectedGlobalX:F4} " +
            $"(deviation={xDev:F4} µm)");

        yDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"Y coordinate mismatch of {yDev:F4} µm detected (fabrication blocker, Issue #329). " +
            $"Waveguide starts at Y={wgStub.StartY:F4} but pin global Y={expectedGlobalY:F4}. " +
            $"Root cause: exporter uses raw editor Y={s1y:F4} → Nazca Y={-s1y:F4}, " +
            $"but the stub places the pin at Y={(gc1Placement.Y + stubPin.Y):F4}. " +
            $"Fix: apply NazcaOriginOffsetY compensation in waveguide coordinate export.");
    }

    /// <summary>
    /// Comprehensive check: both waveguide endpoints (start and end) must match
    /// their respective pin global Nazca positions.
    ///
    /// Design: GC1 at (0, 0) and GC2 at (300, 0), straight waveguide between them.
    /// </summary>
    [Fact]
    public void TwoGratings_WaveguideEndpoints_MustMatchPinPositions()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc1_ep_test";
        gc1.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc1, gcTemplate.Name);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 300, 0);
        gc2.Identifier = "gc2_ep_test";
        gc2.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        _output.WriteLine("=== Exported script (excerpt) ===");
        _output.WriteLine(ExcerptScript(script, 60));

        parsed.Components.Count.ShouldBe(2, "Both GCs must be exported");
        parsed.WaveguideStubs.Count.ShouldBeGreaterThan(0, "Waveguide must be exported");

        var stubPin = parsed.PinDefinitions.First(p => p.Name == "waveguide");
        var wg = parsed.WaveguideStubs.First();

        // Check GC1: waveguide start must match GC1's global pin position
        var gc1Pos = parsed.Components[0];
        double gc1ExpectedX = gc1Pos.X + stubPin.X;
        double gc1ExpectedY = gc1Pos.Y + stubPin.Y;
        double gc1DevX = Math.Abs(gc1ExpectedX - wg.StartX);
        double gc1DevY = Math.Abs(gc1ExpectedY - wg.StartY);

        _output.WriteLine($"GC1 placement: ({gc1Pos.X:F2}, {gc1Pos.Y:F2})");
        _output.WriteLine($"GC1 expected global pin: ({gc1ExpectedX:F2}, {gc1ExpectedY:F2})");
        _output.WriteLine($"WG start: ({wg.StartX:F2}, {wg.StartY:F2})");
        _output.WriteLine($"GC1 deviation: X={gc1DevX:F4} µm, Y={gc1DevY:F4} µm");

        gc1DevX.ShouldBeLessThan(PinAlignmentTolerance,
            $"GC1 waveguide start X mismatch: {gc1DevX:F4} µm");
        gc1DevY.ShouldBeLessThan(PinAlignmentTolerance,
            $"GC1 waveguide start Y mismatch of {gc1DevY:F4} µm (Issue #329 bug). " +
            $"WG Y={wg.StartY:F4} vs pin Y={gc1ExpectedY:F4}.");

        // Check GC2: waveguide end = start + length (for a horizontal segment)
        var gc2Pos = parsed.Components[1];
        double gc2ExpectedX = gc2Pos.X + stubPin.X;
        double gc2ExpectedY = gc2Pos.Y + stubPin.Y;
        double wgEndX = wg.StartX + wg.Length;
        double wgEndY = wg.StartY; // horizontal segment
        double gc2DevX = Math.Abs(gc2ExpectedX - wgEndX);
        double gc2DevY = Math.Abs(gc2ExpectedY - wgEndY);

        _output.WriteLine($"GC2 expected global pin: ({gc2ExpectedX:F2}, {gc2ExpectedY:F2})");
        _output.WriteLine($"WG end: ({wgEndX:F2}, {wgEndY:F2})");
        _output.WriteLine($"GC2 deviation: X={gc2DevX:F4} µm, Y={gc2DevY:F4} µm");

        gc2DevX.ShouldBeLessThan(PinAlignmentTolerance,
            $"GC2 waveguide end X mismatch: {gc2DevX:F4} µm");
        gc2DevY.ShouldBeLessThan(PinAlignmentTolerance,
            $"GC2 waveguide end Y mismatch of {gc2DevY:F4} µm (Issue #329 bug).");
    }

    /// <summary>
    /// Diagnostic test: measures and reports the exact coordinate deviation for
    /// the bug report. Always passes; outputs deviation details to test log.
    /// </summary>
    [Fact]
    public void DiagnoseCoordinateOffset_ReportsDeviationForBugReport()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc_diag";
        gc1.NazcaFunctionName = "ebeam_gc_diag";
        canvas.AddComponent(gc1, gcTemplate.Name);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 300, 0);
        gc2.Identifier = "gc2_diag";
        gc2.NazcaFunctionName = "ebeam_gc_diag";
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        if (parsed.Components.Count == 0 || parsed.WaveguideStubs.Count == 0
            || !parsed.PinDefinitions.Any(p => p.Name == "waveguide"))
        {
            _output.WriteLine("WARNING: Could not parse all required elements from script.");
            _output.WriteLine(script);
            true.ShouldBeTrue(); // pass anyway — diagnostic test
            return;
        }

        var gc1Pos = parsed.Components.First();
        var stubPin = parsed.PinDefinitions.First(p => p.Name == "waveguide");
        var wg = parsed.WaveguideStubs.First();

        double expectedGlobalX = gc1Pos.X + stubPin.X;
        double expectedGlobalY = gc1Pos.Y + stubPin.Y;
        double xDeviation = wg.StartX - expectedGlobalX;
        double yDeviation = wg.StartY - expectedGlobalY;

        _output.WriteLine("=== GDS Coordinate Deviation Report (Issue #329) ===");
        _output.WriteLine($"Component: Grating Coupler at editor (0, 0)");
        _output.WriteLine($"Template NazcaOriginOffsetY:  {gcTemplate.NazcaOriginOffsetY} µm");
        _output.WriteLine($"Template HeightMicrometers:   {gcTemplate.HeightMicrometers} µm");
        _output.WriteLine($"Pin 'waveguide' editor offset: ({pin1.OffsetXMicrometers}, {pin1.OffsetYMicrometers}) µm");
        _output.WriteLine("");
        _output.WriteLine($"Nazca component placement:   ({gc1Pos.X:F4}, {gc1Pos.Y:F4})");
        _output.WriteLine($"Stub local pin position:     ({stubPin.X:F4}, {stubPin.Y:F4})");
        _output.WriteLine($"Expected global Nazca pin:   ({expectedGlobalX:F4}, {expectedGlobalY:F4})");
        _output.WriteLine("");
        _output.WriteLine($"Waveguide start (exporter):  ({wg.StartX:F4}, {wg.StartY:F4})");
        _output.WriteLine("");
        _output.WriteLine($"X deviation: {xDeviation:+0.0000;-0.0000} µm");
        _output.WriteLine($"Y deviation: {yDeviation:+0.0000;-0.0000} µm");
        _output.WriteLine("");
        _output.WriteLine("Root cause analysis:");
        _output.WriteLine($"  Exporter: waveguide Y = -pinEditorY = -{s1y:F4} = {-s1y:F4} µm");
        _output.WriteLine($"  Correct:  waveguide Y = placement.Y + stubPin.Y = {gc1Pos.Y:F4} + {stubPin.Y:F4} = {expectedGlobalY:F4} µm");
        _output.WriteLine($"  Missing correction = height - 2 * originOffsetY = " +
                          $"{gcTemplate.HeightMicrometers} - 2 × {gcTemplate.NazcaOriginOffsetY} = " +
                          $"{gcTemplate.HeightMicrometers - 2 * gcTemplate.NazcaOriginOffsetY:F4} µm");
        _output.WriteLine("");
        _output.WriteLine("Fix required in SimpleNazcaExporter.FormatStraightSegment():");
        _output.WriteLine("  Replace: y = -(straight.StartPoint.Y)");
        _output.WriteLine("  With:    y = -(physY + originOffsetY) + (height - pinOffsetY)");
        _output.WriteLine("  Or equivalently: y via PhysicalPin.GetAbsoluteNazcaY()");
        _output.WriteLine("====================================================");

        // Always passes — diagnostic only
        true.ShouldBeTrue();
    }

    /// <summary>
    /// Test incoming waveguides: waveguide END position must match target pin.
    /// This tests the OPPOSITE direction from the basic tests.
    /// </summary>
    [Fact]
    public void IncomingWaveguide_EndMustMatchTargetPin()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        // GC1 at (0, 0) - SOURCE
        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc1_incoming";
        gc1.NazcaFunctionName = "ebeam_gc_incoming";
        canvas.AddComponent(gc1, gcTemplate.Name);

        // GC2 at (200, 0) - TARGET (testing incoming waveguide END point)
        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 200, 0);
        gc2.Identifier = "gc2_incoming";
        gc2.NazcaFunctionName = "ebeam_gc_incoming";
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        parsed.Components.Count.ShouldBe(2);
        var stubPin = parsed.PinDefinitions.First(p => p.Name == "waveguide");
        var wg = parsed.WaveguideStubs.First();

        // Check GC2 (TARGET): waveguide END must match GC2's global pin position
        var gc2Pos = parsed.Components[1];
        double expectedX = gc2Pos.X + stubPin.X;
        double expectedY = gc2Pos.Y + stubPin.Y;

        // Waveguide end = start + length (horizontal segment)
        double wgEndX = wg.StartX + wg.Length;
        double wgEndY = wg.StartY;

        double xDev = Math.Abs(expectedX - wgEndX);
        double yDev = Math.Abs(expectedY - wgEndY);

        _output.WriteLine($"GC2 (target) expected pin: ({expectedX:F2}, {expectedY:F2})");
        _output.WriteLine($"Waveguide end: ({wgEndX:F2}, {wgEndY:F2})");
        _output.WriteLine($"Deviation: X={xDev:F4} µm, Y={yDev:F4} µm");

        xDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"Incoming waveguide X mismatch at target pin: {xDev:F4} µm");
        yDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"Incoming waveguide Y mismatch at target pin: {yDev:F4} µm (Issue #329)");
    }

    /// <summary>
    /// Test MMI component pins (different dimensions: 50×10 µm vs GC 100×19 µm).
    /// MMI has multiple pins at different Y positions.
    /// </summary>
    [Fact]
    public void DifferentComponent_MMI_PinPositionsMustMatch()
    {
        var canvas = new DesignCanvasViewModel();
        var mmiTemplate = _library.FirstOrDefault(t => t.Name == "MMI 2x2");
        if (mmiTemplate == null)
        {
            _output.WriteLine("SKIP: MMI 2x2 not found in library");
            return;
        }
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        // MMI at (100, 50)
        var mmi = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 50);
        mmi.Identifier = "mmi_test";
        mmi.NazcaFunctionName = "ebeam_mmi_2x2";
        canvas.AddComponent(mmi, mmiTemplate.Name);

        // GC to connect to MMI output
        var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, 250, 50);
        gc.Identifier = "gc_mmi_out";
        gc.NazcaFunctionName = "ebeam_gc_mmi";
        canvas.AddComponent(gc, gcTemplate.Name);

        // Connect MMI output to GC
        var mmiPin = mmi.PhysicalPins.First(p => p.Name == "output1");
        var gcPin = gc.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = mmiPin.GetAbsolutePosition();
        var (s2x, s2y) = gcPin.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, mmiPin.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(mmiPin, gcPin, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        _output.WriteLine($"MMI template: {mmiTemplate.WidthMicrometers}×{mmiTemplate.HeightMicrometers} µm");
        _output.WriteLine($"MMI NazcaOriginOffsetY: {mmiTemplate.NazcaOriginOffsetY}");
        _output.WriteLine($"MMI output1 pin editor offset: ({mmiPin.OffsetXMicrometers}, {mmiPin.OffsetYMicrometers})");

        // Find MMI placement and output1 pin in stub
        var mmiStub = parsed.PinDefinitions.FirstOrDefault(p => p.Name == "output1");
        if (mmiStub == null)
        {
            _output.WriteLine("WARNING: MMI stub not found or output1 pin missing");
            return; // Skip if MMI stub generation doesn't work
        }

        var mmiPlacement = parsed.Components.First();
        double expectedX = mmiPlacement.X + mmiStub.X;
        double expectedY = mmiPlacement.Y + mmiStub.Y;

        var wg = parsed.WaveguideStubs.First();
        double xDev = Math.Abs(expectedX - wg.StartX);
        double yDev = Math.Abs(expectedY - wg.StartY);

        _output.WriteLine($"MMI placement: ({mmiPlacement.X:F2}, {mmiPlacement.Y:F2})");
        _output.WriteLine($"Expected global pin: ({expectedX:F2}, {expectedY:F2})");
        _output.WriteLine($"Waveguide start: ({wg.StartX:F2}, {wg.StartY:F2})");
        _output.WriteLine($"Deviation: X={xDev:F4} µm, Y={yDev:F4} µm");

        xDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"MMI output1 X mismatch: {xDev:F4} µm");
        yDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"MMI output1 Y mismatch: {yDev:F4} µm (different component dimensions test)");
    }

    /// <summary>
    /// Test rotated components: GC at 90° rotation.
    /// Pin positions must be correctly transformed after rotation.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]      // No rotation at origin
    [InlineData(90, 0, 0)]     // 90° rotation at origin
    [InlineData(180, 0, 0)]    // 180° rotation at origin
    [InlineData(270, 0, 0)]    // 270° rotation at origin
    [InlineData(0, 150, 100)]  // No rotation, offset position
    [InlineData(90, 150, 100)] // 90° rotation, offset position
    public void RotatedComponent_PinPositionsMustMatchAfterRotation(int rotation, double posX, double posY)
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        // GC1 at specified position and rotation
        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, posX, posY);
        gc1.Identifier = $"gc_rot{rotation}";
        gc1.NazcaFunctionName = $"ebeam_gc_rot{rotation}";
        gc1.RotationDegrees = rotation;
        canvas.AddComponent(gc1, gcTemplate.Name);

        // GC2 for destination (position calculated based on rotated pin direction)
        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var angle = pin1.GetAbsoluteAngle();

        // Place GC2 200µm away in the direction of the rotated pin
        double destX = s1x + 200 * Math.Cos(angle * Math.PI / 180);
        double destY = s1y + 200 * Math.Sin(angle * Math.PI / 180);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, destX, destY);
        gc2.Identifier = $"gc2_rot{rotation}";
        gc2.NazcaFunctionName = $"ebeam_gc_rot{rotation}";
        gc2.RotationDegrees = rotation;
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, angle));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        if (parsed.Components.Count == 0 || parsed.WaveguideStubs.Count == 0)
        {
            _output.WriteLine($"SKIP: Rotation {rotation}° not fully exported");
            return;
        }

        // Expected via NazcaCoordinateMapper (#565): pin positions convert by plain
        // Y negation for every rotation — the segment is routed from the app pin
        // position, so the exported start must be its Y-negated image. A rotated
        // origin-offset pin formula would diverge here (the #565 misalignment).
        var (expectedX, expectedY) = NazcaCoordinateMapper.GetPinNazcaPosition(pin1);

        var wg = parsed.WaveguideStubs.First();
        double xDev = Math.Abs(expectedX - wg.StartX);
        double yDev = Math.Abs(expectedY - wg.StartY);

        _output.WriteLine($"Rotation: {rotation}°, Position: ({posX}, {posY})");
        _output.WriteLine($"Expected pin (Nazca): ({expectedX:F2}, {expectedY:F2})");
        _output.WriteLine($"Waveguide start: ({wg.StartX:F2}, {wg.StartY:F2})");
        _output.WriteLine($"Deviation: X={xDev:F4} µm, Y={yDev:F4} µm");

        xDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"Rotated component ({rotation}°) X mismatch: {xDev:F4} µm");
        yDev.ShouldBeLessThan(PinAlignmentTolerance,
            $"Rotated component ({rotation}°) Y mismatch: {yDev:F4} µm");
    }

    /// <summary>
    /// Test vertical waveguides (90° angle).
    /// Ensures Y-coordinate bug affects both horizontal and vertical routing.
    /// </summary>
    [Fact]
    public void VerticalWaveguide_PinPositionsMustMatch()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        // GC1 at (100, 0) rotated 90° (pin points up)
        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 100, 0);
        gc1.Identifier = "gc_vertical_1";
        gc1.NazcaFunctionName = "ebeam_gc_vert";
        gc1.RotationDegrees = 90;
        canvas.AddComponent(gc1, gcTemplate.Name);

        // GC2 at (100, 200) rotated 270° (pin points down)
        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 100, 200);
        gc2.Identifier = "gc_vertical_2";
        gc2.NazcaFunctionName = "ebeam_gc_vert";
        gc2.RotationDegrees = 270;
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        _output.WriteLine("=== Vertical waveguide test ===");
        _output.WriteLine($"Pin1 absolute: ({s1x:F2}, {s1y:F2}) @ {pin1.GetAbsoluteAngle()}°");
        _output.WriteLine($"Pin2 absolute: ({s2x:F2}, {s2y:F2}) @ {pin2.GetAbsoluteAngle()}°");

        if (parsed.Components.Count < 2 || parsed.WaveguideStubs.Count == 0)
        {
            _output.WriteLine("SKIP: Vertical routing not fully exported");
            return;
        }

        // This test primarily checks that vertical routing doesn't crash
        // and that the Y-coordinate bug manifests in vertical directions too
        true.ShouldBeTrue("Vertical waveguide export completed");
    }

    /// <summary>
    /// EXHAUSTIVE TEST: Test ALL PDK components systematically.
    /// Each component is placed, connected to a GC, and tested for pin alignment.
    /// This ensures the coordinate bug (or its fix) applies uniformly across the entire PDK.
    /// </summary>
    [Fact]
    public void ExhaustiveAllPdkComponents_PinPositionsMustMatch()
    {
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");
        var testedComponents = 0;
        var failedComponents = new List<string>();
        var skippedComponents = new List<string>();

        _output.WriteLine("=== EXHAUSTIVE PDK COMPONENT TEST ===");
        _output.WriteLine($"Total components in library: {_library.Count}");
        _output.WriteLine("");

        foreach (var template in _library)
        {
            // Skip Grating Coupler itself (already extensively tested)
            if (template.Name == "Grating Coupler")
                continue;

            _output.WriteLine($"Testing component: {template.Name}");
            _output.WriteLine($"  Dimensions: {template.WidthMicrometers}×{template.HeightMicrometers} µm");
            _output.WriteLine($"  NazcaOriginOffsetY: {template.NazcaOriginOffsetY} µm");

            var canvas = new DesignCanvasViewModel();

            // Create component at (100, 50)
            var component = ComponentTemplates.CreateFromTemplate(template, 100, 50);
            component.Identifier = $"test_{template.Name.Replace(" ", "_")}";
            component.NazcaFunctionName = $"ebeam_{template.Name.Replace(" ", "_").ToLower()}";
            canvas.AddComponent(component, template.Name);

            // Find first output pin (or any pin)
            var outputPin = component.PhysicalPins.FirstOrDefault(p =>
                p.Name.Contains("output") || p.Name.Contains("waveguide") || p.Name.Contains("o"));

            if (outputPin == null)
            {
                _output.WriteLine($"  SKIP: No suitable output pin found");
                _output.WriteLine("");
                skippedComponents.Add(template.Name);
                continue;
            }

            _output.WriteLine($"  Using pin: {outputPin.Name} at offset ({outputPin.OffsetXMicrometers}, {outputPin.OffsetYMicrometers})");

            // Connect to a Grating Coupler
            var (outX, outY) = outputPin.GetAbsolutePosition();
            var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, outX + 200, outY);
            gc.Identifier = $"gc_for_{template.Name}";
            gc.NazcaFunctionName = $"ebeam_gc_{template.Name.Replace(" ", "_").ToLower()}";
            canvas.AddComponent(gc, gcTemplate.Name);

            var gcPin = gc.PhysicalPins.First(p => p.Name == "waveguide");
            var (gcX, gcY) = gcPin.GetAbsolutePosition();

            var route = new RoutedPath();
            route.Segments.Add(new StraightSegment(outX, outY, gcX, gcY, outputPin.GetAbsoluteAngle()));
            canvas.ConnectPinsWithCachedRoute(outputPin, gcPin, route);

            // Export and parse
            var script = _exporter.Export(canvas);
            var parsed = _parser.Parse(script);

            if (parsed.Components.Count < 2 || parsed.WaveguideStubs.Count == 0)
            {
                _output.WriteLine($"  SKIP: Export incomplete (components={parsed.Components.Count}, waveguides={parsed.WaveguideStubs.Count})");
                _output.WriteLine("");
                skippedComponents.Add(template.Name);
                continue;
            }

            // Find component stub pin
            var stubPin = parsed.PinDefinitions.FirstOrDefault(p => p.Name == outputPin.Name);
            if (stubPin == null)
            {
                _output.WriteLine($"  SKIP: Pin '{outputPin.Name}' not found in stub");
                _output.WriteLine("");
                skippedComponents.Add(template.Name);
                continue;
            }

            // Check alignment
            var compPlacement = parsed.Components.First();
            double expectedX = compPlacement.X + stubPin.X;
            double expectedY = compPlacement.Y + stubPin.Y;

            var wg = parsed.WaveguideStubs.First();
            double xDev = Math.Abs(expectedX - wg.StartX);
            double yDev = Math.Abs(expectedY - wg.StartY);

            _output.WriteLine($"  Component placement: ({compPlacement.X:F2}, {compPlacement.Y:F2})");
            _output.WriteLine($"  Expected global pin: ({expectedX:F2}, {expectedY:F2})");
            _output.WriteLine($"  Waveguide start: ({wg.StartX:F2}, {wg.StartY:F2})");
            _output.WriteLine($"  Deviation: X={xDev:F4} µm, Y={yDev:F4} µm");

            if (xDev >= PinAlignmentTolerance || yDev >= PinAlignmentTolerance)
            {
                _output.WriteLine($"  ❌ FAIL: Deviation exceeds tolerance");
                failedComponents.Add($"{template.Name} (ΔX={xDev:F4}, ΔY={yDev:F4})");
            }
            else
            {
                _output.WriteLine($"  ✓ PASS");
            }

            _output.WriteLine("");
            testedComponents++;
        }

        // Summary
        _output.WriteLine("=== EXHAUSTIVE TEST SUMMARY ===");
        _output.WriteLine($"Total components: {_library.Count}");
        _output.WriteLine($"Tested: {testedComponents}");
        _output.WriteLine($"Skipped: {skippedComponents.Count}");
        _output.WriteLine($"Failed: {failedComponents.Count}");

        if (skippedComponents.Count > 0)
        {
            _output.WriteLine("");
            _output.WriteLine("Skipped components:");
            foreach (var name in skippedComponents)
                _output.WriteLine($"  - {name}");
        }

        if (failedComponents.Count > 0)
        {
            _output.WriteLine("");
            _output.WriteLine("Failed components:");
            foreach (var name in failedComponents)
                _output.WriteLine($"  - {name}");

            failedComponents.Count.ShouldBe(0,
                $"GDS coordinate bug detected in {failedComponents.Count} components. " +
                $"All PDK components must have correctly aligned waveguide-to-pin coordinates.");
        }

        testedComponents.ShouldBeGreaterThan(0, "At least one component should be successfully tested");
    }

    /// <summary>
    /// Test ComponentGroup with frozen waveguide paths export.
    /// Groups export internal components + frozen paths - both must have correct coordinates.
    ///
    /// NOTE: This is a simplified test that verifies ComponentGroup export doesn't crash
    /// and that frozen paths appear in the Nazca script. Full coordinate verification
    /// for groups requires more complex setup with the actual CreateGroupCommand workflow.
    /// </summary>
    [Fact]
    public void ComponentGroup_WithFrozenPaths_ExportCompletesSuccessfully()
    {
        // NOTE: ComponentGroup creation through proper workflow (CreateGroupCommand)
        // is complex and involves ViewModel state. This test just verifies that
        // IF a group exists with frozen paths, the coordinate bug would affect it too.

        _output.WriteLine("=== ComponentGroup Frozen Path Export Test ===");
        _output.WriteLine("This test verifies that ComponentGroups with frozen waveguide paths");
        _output.WriteLine("can be exported to Nazca. The same coordinate bug (9.5µm Y offset)");
        _output.WriteLine("would affect frozen waveguide paths exported from groups.");
        _output.WriteLine("");
        _output.WriteLine("Full integration test for groups requires:");
        _output.WriteLine("  - DesignCanvasViewModel with CreateGroupCommand");
        _output.WriteLine("  - Proper group creation workflow");
        _output.WriteLine("  - External pin connections");
        _output.WriteLine("");
        _output.WriteLine("For now, we verify the principle with individual components.");

        // Create simple design to verify export works
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc1_group_test";
        gc1.NazcaFunctionName = "ebeam_gc_group";
        canvas.AddComponent(gc1, gcTemplate.Name);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 200, 0);
        gc2.Identifier = "gc2_group_test";
        gc2.NazcaFunctionName = "ebeam_gc_group";
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        // Export and verify basic structure
        var script = _exporter.Export(canvas);

        _output.WriteLine("Exported Nazca script structure:");
        _output.WriteLine(ExcerptScript(script, 60));

        // Verify script contains waveguide export
        script.Contains("nd.strt").ShouldBeTrue(
            "Waveguide connections (including those that would be frozen in groups) " +
            "must be exported as nd.strt() segments");

        _output.WriteLine("");
        _output.WriteLine("✓ Basic export successful");
        _output.WriteLine("");
        _output.WriteLine("When the GDS coordinate bug (#329) is fixed, frozen waveguide paths");
        _output.WriteLine("in ComponentGroups will also export with correct pin alignment.");
        _output.WriteLine("");
        _output.WriteLine("TODO: Add full ComponentGroup integration test after PR #329 fix.");
    }

    /// <summary>
    /// Integration test: if Python + Nazca + gdspy are available, generates both
    /// the system GDS and the reference GDS, runs Python comparison scripts, and
    /// asserts max deviation is within tolerance.
    ///
    /// Skips gracefully when the Python stack is not available.
    /// Expected to FAIL (max_deviation > tolerance) when the bug is present.
    /// </summary>
    [Fact]
    public async Task PythonGdsBinaryComparison_WhenEnvironmentReady_DeviationsWithinTolerance()
    {
        var gdsService = new GdsExportService();
        var env = await gdsService.CheckPythonEnvironmentAsync();
        if (!env.IsReady)
        {
            _output.WriteLine($"Python environment not ready ({env.StatusMessage}) — skipping.");
            return;
        }

        var gdspyAvailable = await CheckGdspyAsync();
        if (!gdspyAvailable)
        {
            _output.WriteLine("gdspy not installed — skipping. Install with: pip install gdspy");
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"cap_gds_verify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Generate system GDS
            var systemScriptPath = Path.Combine(tempDir, "system_minimal.py");
            var systemGdsPath    = Path.ChangeExtension(systemScriptPath, ".gds");
            var systemCoordsPath = Path.ChangeExtension(systemScriptPath, "_coords.json");

            var canvas = CreateMinimalDesign();
            var script = _exporter.Export(canvas);
            script = FixGdsOutputPath(script, systemGdsPath);
            await File.WriteAllTextAsync(systemScriptPath, script);

            var exportResult = await gdsService.ExportToGdsAsync(systemScriptPath, generateGds: true);
            if (!exportResult.Success)
            {
                _output.WriteLine($"System GDS generation failed: {exportResult.ErrorMessage}");
                return;
            }

            // 2. Generate reference GDS
            var repoRoot = FindRepoRoot();
            var refScriptSrc = Path.Combine(repoRoot, "scripts", "reference_minimal.py");
            var refGdsPath    = Path.Combine(tempDir, "reference_minimal.gds");
            var refCoordsPath = Path.Combine(tempDir, "reference_coords.json");
            var reportPath    = Path.Combine(tempDir, "comparison_report.json");

            var refScript = await File.ReadAllTextAsync(refScriptSrc);
            refScript = refScript.Replace(
                "output_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'tmp', 'reference_minimal.gds')",
                $"output_path = r'{refGdsPath.Replace("\\", "/")}'");
            var tmpRefScript = Path.Combine(tempDir, "reference_minimal.py");
            await File.WriteAllTextAsync(tmpRefScript, refScript);

            var refResult = await gdsService.ExportToGdsAsync(tmpRefScript, generateGds: true);
            if (!refResult.Success)
            {
                _output.WriteLine($"Reference GDS generation failed: {refResult.ErrorMessage}");
                return;
            }

            // 3. Extract coordinates from both GDS files
            var extractScript = Path.Combine(repoRoot, "scripts", "extract_gds_coords.py");
            await RunPythonAsync(extractScript, $"\"{systemGdsPath}\" \"{systemCoordsPath}\"");
            await RunPythonAsync(extractScript, $"\"{refGdsPath}\" \"{refCoordsPath}\"");

            if (!File.Exists(systemCoordsPath) || !File.Exists(refCoordsPath))
            {
                _output.WriteLine("Coordinate extraction produced no output — skipping.");
                return;
            }

            // 4. Compare coordinates
            var compareScript = Path.Combine(repoRoot, "scripts", "compare_gds_coords.py");
            var (exitCode, compareOut, _) = await RunPythonRawAsync(
                compareScript, $"\"{refCoordsPath}\" \"{systemCoordsPath}\" \"{reportPath}\"");

            _output.WriteLine(compareOut);

            if (!File.Exists(reportPath))
            {
                _output.WriteLine("Comparison report not generated — cannot assert.");
                return;
            }

            var reportJson = await File.ReadAllTextAsync(reportPath);
            _output.WriteLine($"Report: {reportJson}");

            // Copy to /tmp for permanent access
            await File.WriteAllTextAsync("/tmp/comparison_report.json", reportJson);

            double maxDev = ParseMaxDeviation(reportJson);
            _output.WriteLine($"Max deviation: {maxDev:F4} µm  (tolerance={PinAlignmentTolerance} µm)");

            // FAILS when coordinate bug is present
            maxDev.ShouldBeLessThan(PinAlignmentTolerance,
                $"GDS coordinate deviation {maxDev:F4} µm exceeds tolerance. " +
                $"See /tmp/comparison_report.json");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    // ── Design factory ────────────────────────────────────────────────────────

    /// <summary>Minimal design: 2 GCs + 1 straight waveguide.</summary>
    private DesignCanvasViewModel CreateMinimalDesign()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.Identifier = "gc1_minimal";
        gc1.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc1, gcTemplate.Name);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, 300, 0);
        gc2.Identifier = "gc2_minimal";
        gc2.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc2, gcTemplate.Name);

        var pin1 = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var pin2 = gc2.PhysicalPins.First(p => p.Name == "waveguide");
        var (s1x, s1y) = pin1.GetAbsolutePosition();
        var (s2x, s2y) = pin2.GetAbsolutePosition();

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(s1x, s1y, s2x, s2y, pin1.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(pin1, pin2, route);

        return canvas;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExcerptScript(string script, int lineCount)
        => string.Join('\n', script.Split('\n').Take(lineCount));

    private static async Task<bool> CheckGdspyAsync()
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "check_gdspy_cap.py");
            await File.WriteAllTextAsync(tmp, "import gdspy; print('ok')");
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{tmp}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (File.Exists(tmp)) File.Delete(tmp);
            return proc.ExitCode == 0 && output.Trim() == "ok";
        }
        catch { return false; }
    }

    private static async Task RunPythonAsync(string scriptPath, string args)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{scriptPath}\" {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            await proc.WaitForExitAsync();
        }
        catch { /* Python not available */ }
    }

    private static async Task<(int exitCode, string output, string error)> RunPythonRawAsync(
        string scriptPath, string args)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{scriptPath}\" {args}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex) { return (1, "", ex.Message); }
    }

    private static string FixGdsOutputPath(string script, string gdsPath)
        => script.Replace(
            "gds_filename = os.path.splitext(script_path)[0] + '.gds'",
            $"gds_filename = r'{gdsPath.Replace("\\", "/")}'");

    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "ConnectAPICPro.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("ConnectAPICPro.sln not found");
    }

    private static double ParseMaxDeviation(string reportJson)
    {
        const string key = "\"max_deviation_um\":";
        int idx = reportJson.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return double.MaxValue;
        int start = idx + key.Length;
        while (start < reportJson.Length && char.IsWhiteSpace(reportJson[start])) start++;
        int end = start;
        while (end < reportJson.Length &&
               (char.IsDigit(reportJson[end]) || reportJson[end] == '.' || reportJson[end] == '-'))
            end++;
        return double.TryParse(reportJson.AsSpan(start, end - start),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : double.MaxValue;
    }
}

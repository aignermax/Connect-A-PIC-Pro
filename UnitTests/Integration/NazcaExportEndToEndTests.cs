using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// End-to-end integration tests for Nazca export validation.
/// Tests the complete pipeline: UI → Backend → Nazca code → Validation.
/// Reproduces issues #66 (position offset) and #69 (dimension mismatch).
/// </summary>
public class NazcaExportEndToEndTests
{
    private const double PositionTolerance = 0.02;

    [Fact]
    public void Export_TwoConnectedComponents_WaveguideEndsMatchPins()
    {
        // Arrange: Create design with 2 connected components
        var canvas = new DesignCanvasViewModel();

        var mmi = CreateTestComponent("MMI_1x2", 0, 0, 100, 50);
        mmi.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out1",
            ParentComponent = mmi,
            OffsetXMicrometers = 100,
            OffsetYMicrometers = 25,
            AngleDegrees = 0
        });
        canvas.AddComponent(mmi, "MMI_1x2");

        var detector = CreateTestComponent("Detector", 250, 0, 50, 50);
        detector.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in",
            ParentComponent = detector,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180
        });
        canvas.AddComponent(detector, "Detector");

        // Create and route connection with straight path
        AddConnectionToCanvas(canvas, mmi.PhysicalPins[0], detector.PhysicalPins[0]);

        // Act: Export to Nazca Python code
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        // Assert: Validate exported code
        var validator = new ExportValidator();
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        var connections = canvas.Connections.Select(vm => vm.Connection).ToList();

        var result = validator.Validate(components, connections, nazcaCode);

        // Should have no errors
        result.IsValid.ShouldBeTrue(
            $"Validation failed with {result.FailedChecks} errors:\n" +
            string.Join("\n", result.Errors));
    }

    [Fact]
    public void Export_RotatedComponent_PositionAndRotationCorrect()
    {
        // Arrange: Create rotated component
        var canvas = new DesignCanvasViewModel();
        var component = CreateTestComponent("TestComp", 100, 50, 80, 40);
        component.RotationDegrees = 90;
        component.NazcaOriginOffsetX = 0;
        component.NazcaOriginOffsetY = 40; // Height offset for Y-flip
        canvas.AddComponent(component, "TestComp");

        // Act: Export
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        // Assert: Parse and validate
        var parser = new NazcaCodeParser();
        var parsed = parser.Parse(nazcaCode);

        parsed.Components.Count.ShouldBe(1);

        // Check rotation is inverted for Nazca (Y-axis flip)
        parsed.Components[0].RotationDegrees.ShouldBe(-90, tolerance: 0.1);
    }

    [Fact]
    public void Export_MultipleRotations_AllCorrect()
    {
        // Test all common rotations: 0°, 90°, 180°, 270°
        var canvas = new DesignCanvasViewModel();

        var rotations = new[] { 0, 90, 180, 270 };
        for (int i = 0; i < rotations.Length; i++)
        {
            var comp = CreateTestComponent($"Comp_{i}", i * 150, 0, 80, 40);
            comp.RotationDegrees = rotations[i];
            comp.NazcaOriginOffsetX = 0;
            comp.NazcaOriginOffsetY = 40;
            canvas.AddComponent(comp, $"Comp_{i}");
        }

        // Act: Export
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        // Assert: Parse and check all rotations
        var parser = new NazcaCodeParser();
        var parsed = parser.Parse(nazcaCode);

        parsed.Components.Count.ShouldBe(4);

        for (int i = 0; i < rotations.Length; i++)
        {
            var expectedNazcaRotation = -rotations[i];
            var actualRotation = parsed.Components[i].RotationDegrees;

            // Normalize to -180 to 180 range
            actualRotation = ((actualRotation + 180) % 360) - 180;
            expectedNazcaRotation = ((expectedNazcaRotation + 180) % 360) - 180;

            actualRotation.ShouldBe(expectedNazcaRotation, tolerance: 0.1,
                $"Component {i} rotation mismatch");
        }
    }

    [Fact]
    public void Export_ComponentDimensions_MatchStubDefinition()
    {
        // Arrange: Use demo_pdk component with no origin offset
        var canvas = new DesignCanvasViewModel();
        var component = CreateTestComponent("MMI_2x2", 0, 0, 120, 50);
        component.NazcaFunctionName = "demo_pdk.mmi_test"; // demo_pdk → generates stub
        component.NazcaOriginOffsetX = 0;
        component.NazcaOriginOffsetY = 0; // No offset
        canvas.AddComponent(component, "MMI_2x2");

        // Act: Export
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        // Assert: Check dimensions in stub comment
        nazcaCode.ShouldContain("(120x50 µm)");

        // Check polygon dimensions in stub: bbox [0,-H] .. [W,0] around the org pin
        // (NazcaCoordinateMapper contract for calibration (0,0), #565).
        nazcaCode.ShouldContain("nd.Polygon(points=[(0.00,-50.00),(120.00,-50.00),(120.00,0.00),(0.00,0.00)], layer=1)");
    }

    [Fact]
    public void Export_PinPositions_CorrectInStubDefinition()
    {
        // Arrange: Use demo_pdk.mmi2x2 which has NazcaOriginOffset=(0,0) (no offset)
        var canvas = new DesignCanvasViewModel();
        var component = CreateTestComponent("MMI_2x2", 0, 0, 120, 50);
        component.NazcaFunctionName = "demo_pdk.mmi2x2";
        component.NazcaOriginOffsetX = 0;
        component.NazcaOriginOffsetY = 0; // Demo PDK MMI has no offset

        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "a0",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 12.5,
            AngleDegrees = 180
        });
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "b0",
            ParentComponent = component,
            OffsetXMicrometers = 120,
            OffsetYMicrometers = 12.5,
            AngleDegrees = 0
        });

        canvas.AddComponent(component, "MMI_2x2");

        // Act: Export
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        // Assert: Parse pins from stub
        var parser = new NazcaCodeParser();
        var parsed = parser.Parse(nazcaCode);

        parsed.PinDefinitions.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Stub pins render at the plain Y negation of the app pin offsets relative
        // to org (NazcaCoordinateMapper contract, #565): with calibration (0,0)
        // pin a0 at editor offset (0, 12.5) sits at local (0, -12.5).
        var a0Pin = parsed.PinDefinitions.FirstOrDefault(p => p.Name == "a0");
        a0Pin.ShouldNotBeNull();
        a0Pin.X.ShouldBe(0.00, tolerance: 0.01);
        a0Pin.Y.ShouldBe(-12.50, tolerance: 0.01);
        a0Pin.AngleDegrees.ShouldBe(-180, tolerance: 0.1);

        // Find b0 pin
        var b0Pin = parsed.PinDefinitions.FirstOrDefault(p => p.Name == "b0");
        b0Pin.ShouldNotBeNull();
        b0Pin.X.ShouldBe(120.00, tolerance: 0.01);
        b0Pin.Y.ShouldBe(-12.50, tolerance: 0.01);
        b0Pin.AngleDegrees.ShouldBe(0, tolerance: 0.1);
    }

    [Fact]
    public void Export_ComplexDesign_AllElementsValid()
    {
        // Arrange: Complex design with multiple components and connections
        var canvas = new DesignCanvasViewModel();

        var source = CreateTestComponent("Source", 0, 0, 50, 50);
        source.PhysicalPins.Add(CreatePin(source, "out", 50, 25, 0));
        canvas.AddComponent(source, "Source");

        var mmi = CreateTestComponent("MMI_1x2", 150, 0, 100, 50);
        mmi.PhysicalPins.Add(CreatePin(mmi, "in", 0, 25, 180));
        mmi.PhysicalPins.Add(CreatePin(mmi, "out0", 100, 12.5, 0));
        mmi.PhysicalPins.Add(CreatePin(mmi, "out1", 100, 37.5, 0));
        canvas.AddComponent(mmi, "MMI_1x2");

        var det1 = CreateTestComponent("Det1", 350, -30, 50, 50);
        det1.PhysicalPins.Add(CreatePin(det1, "in", 0, 25, 180));
        canvas.AddComponent(det1, "Det1");

        var det2 = CreateTestComponent("Det2", 350, 30, 50, 50);
        det2.PhysicalPins.Add(CreatePin(det2, "in", 0, 25, 180));
        canvas.AddComponent(det2, "Det2");

        // Add connections
        AddConnectionToCanvas(canvas, source.PhysicalPins[0], mmi.PhysicalPins[0]);
        AddConnectionToCanvas(canvas, mmi.PhysicalPins[1], det1.PhysicalPins[0]);
        AddConnectionToCanvas(canvas, mmi.PhysicalPins[2], det2.PhysicalPins[0]);

        // Act: Export and validate
        var exporter = new SimpleNazcaExporter();
        var nazcaCode = exporter.Export(canvas);

        var validator = new ExportValidator();
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        var connections = canvas.Connections.Select(vm => vm.Connection).ToList();

        var result = validator.Validate(components, connections, nazcaCode);

        // Assert: Should be valid
        result.Errors.Count.ShouldBe(0,
            $"Validation errors:\n{string.Join("\n", result.Errors)}");
        result.PassedChecks.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Creates a test component with specified properties.
    /// </summary>
    private static Component CreateTestComponent(
        string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: $"test_{identifier.ToLower()}",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>()
        );

        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;
        component.NazcaOriginOffsetX = 0;
        component.NazcaOriginOffsetY = height; // Default Y-flip offset

        return component;
    }

    /// <summary>
    /// Creates a physical pin for a component.
    /// </summary>
    private static PhysicalPin CreatePin(
        Component parent, string name, double offsetX, double offsetY, double angle)
    {
        return new PhysicalPin
        {
            Name = name,
            ParentComponent = parent,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angle
        };
    }

    /// <summary>
    /// Adds a waveguide connection with a straight segment to the canvas.
    /// </summary>
    private static void AddConnectionToCanvas(
        DesignCanvasViewModel canvas, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, startY, endX, endY, startPin.GetAbsoluteAngle()));

        canvas.ConnectionManager.AddConnectionWithCachedRoute(startPin, endPin, path);
    }
}

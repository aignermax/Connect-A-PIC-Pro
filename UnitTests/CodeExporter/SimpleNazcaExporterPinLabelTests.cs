using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.CodeExporter;

/// <summary>
/// Tests for the pin/port label emission of <see cref="SimpleNazcaExporter"/> (issue #808):
/// stub cells carry one GDS TEXT label per optical pin and the design's external ports
/// (fiber couplers) carry top-cell labels — all on the gdsfactory port-label layer (1, 10)
/// that the GDS re-import pin detector reads. Labels are emitted as nd.Annotation because
/// nd.text renders stroked polygons, not GDS TEXT records.
/// </summary>
public class SimpleNazcaExporterPinLabelTests
{
    [Fact]
    public void Export_InputAndOutputCouplers_EmitsTopCellPortLabelsAtPinPositions()
    {
        // Arrange: one input coupler (laser on) and one output coupler (laser off, #690)
        var canvas = new DesignCanvasViewModel();

        var input = CreateGratingCoupler("GCIn", physicalX: 100, physicalY: 50);
        canvas.Components.Add(new ComponentViewModel(input));

        var output = CreateGratingCoupler("GCOut", physicalX: 500, physicalY: 50);
        output.LaserEnabled = false;
        canvas.Components.Add(new ComponentViewModel(output));

        // Act
        var script = new SimpleNazcaExporter().Export(canvas);

        // Assert: label at the pin's world position — app (X+39.969, Y+13.669) with the
        // exporter's plain Y negation → nazca (139.97, -63.67) / (539.97, -63.67).
        script.ShouldContain("nd.Annotation(text='GCIn_port 2', layer=(1, 10)).put(139.97, -63.67)");
        script.ShouldContain("nd.Annotation(text='GCOut_port 2', layer=(1, 10)).put(539.97, -63.67)");

        // Top-cell level: the port labels appear inside create_design, after the design
        // cell opens (stub-cell labels, named 'port 2' without the component prefix,
        // are emitted before it).
        var designIndex = script.IndexOf("with nd.Cell(name='ConnectAPIC_Design')", StringComparison.Ordinal);
        designIndex.ShouldBeGreaterThanOrEqualTo(0);
        script.IndexOf("nd.Annotation(text='GCIn_port 2'", StringComparison.Ordinal)
            .ShouldBeGreaterThan(designIndex);
        script.IndexOf("nd.Annotation(text='GCOut_port 2'", StringComparison.Ordinal)
            .ShouldBeGreaterThan(designIndex);
    }

    [Fact]
    public void Export_ComponentStub_EmitsOneLabelPerPinAtPinPositions()
    {
        // Arrange: a PDK component with two named optical pins (not a fiber coupler,
        // so the only labels in the script are the stub-cell pin labels)
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent("ebeam_mmi1x2_test", "MMI1");
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 20;
        comp.NazcaOriginOffsetX = 0;
        comp.NazcaOriginOffsetY = 10;
        AddPin(comp, "o1", offsetX: 0, offsetY: 10, angleDegrees: 180);
        AddPin(comp, "o2", offsetX: 50, offsetY: 10, angleDegrees: 0);
        canvas.Components.Add(new ComponentViewModel(comp));

        // Act
        var script = new SimpleNazcaExporter().Export(canvas);

        // Assert: pin-local stub coordinates are (OffsetX-ox, oy-OffsetY) → o1 (0, 0), o2 (50, 0)
        script.ShouldContain("nd.Pin('o1').put(0.00, 0.00, -180)");
        script.ShouldContain("nd.Annotation(text='o1', layer=(1, 10)).put(0.00, 0.00)");
        script.ShouldContain("nd.Annotation(text='o2', layer=(1, 10)).put(50.00, 0.00)");

        // Exactly one label per pin, and no top-cell port label for a non-coupler
        CountOccurrences(script, "nd.Annotation(").ShouldBe(2);
        script.ShouldNotContain("text='MMI1_");
    }

    [Fact]
    public void Export_ParametricStraightStub_EmitsPinLabelsAtPinPositions()
    {
        // Arrange: a 100 µm demo_pdk straight (pins a0 at x=0, b0 at x=length)
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CreateDemoPdkStraightWaveguide(100)));

        // Act
        var script = new SimpleNazcaExporter().Export(canvas);

        // Assert: labels mirror the nd.Pin anchors — a0 at x=0, b0 at x=length, both at
        // the stub centre line y = anchorY - pinOffsetY = 0 - 5 = -5
        script.ShouldContain("nd.Pin('a0').put(0, -5.00, -180)");
        script.ShouldContain("nd.Annotation(text='a0', layer=(1, 10)).put(0, -5.00)");
        script.ShouldContain("nd.Annotation(text='b0', layer=(1, 10)).put(length, -5.00)");
    }

    [Fact]
    public void Export_PortNameWithQuoteAndBackslash_IsEscapedForPython()
    {
        // Arrange: a coupler whose identifier contains a single quote and a backslash —
        // unescaped, both would break the single-quoted Python string literal
        var canvas = new DesignCanvasViewModel();
        var comp = CreateGratingCoupler("GC 'evil' \\test", physicalX: 0, physicalY: 0);
        canvas.Components.Add(new ComponentViewModel(comp));

        // Act
        var script = new SimpleNazcaExporter().Export(canvas);

        // Assert: backslash first doubled, then the quote backslash-escaped
        script.ShouldContain(@"nd.Annotation(text='GC \'evil\' \\test_port 2', layer=(1, 10))");
    }

    [Fact]
    public void Export_AnalysisToolComponent_EmitsNoLabels()
    {
        // Arrange: a virtual analysis tool — named like a coupler, so the port-label
        // classifier WOULD match if the analysis-tool guard did not skip it first
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(Component.AnalysisToolNazcaSentinel, "Grating Coupler Analyzer");
        comp.WidthMicrometers = 40;
        comp.HeightMicrometers = 27;
        AddPin(comp, "port 2", offsetX: 39.969, offsetY: 13.669, angleDegrees: 0);
        canvas.Components.Add(new ComponentViewModel(comp));

        // Act
        var script = new SimpleNazcaExporter().Export(canvas);

        // Assert: analysis tools are virtual — no stub, no placement, no labels
        script.ShouldNotContain("nd.Annotation(");
    }

    // ── Test fixtures ────────────────────────────────────────────────────────

    /// <summary>SiEPIC-like grating coupler with a single optical pin ("port 2").</summary>
    private static Component CreateGratingCoupler(string identifier, double physicalX, double physicalY)
    {
        var comp = CreateComponent("ebeam_gc_te1550", identifier);
        comp.WidthMicrometers = 40.019;
        comp.HeightMicrometers = 27.169;
        comp.NazcaOriginOffsetX = 39.969;
        comp.NazcaOriginOffsetY = 13.5;
        comp.PhysicalX = physicalX;
        comp.PhysicalY = physicalY;
        AddPin(comp, "port 2", offsetX: 39.969, offsetY: 13.669, angleDegrees: 0);
        return comp;
    }

    private static Component CreateComponent(string nazcaFunctionName, string identifier)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunctionName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0);
    }

    private static Component CreateDemoPdkStraightWaveguide(double lengthMicrometers)
    {
        var component = CreateComponent("demo_pdk.straight", $"Straight Waveguide {lengthMicrometers}µm");
        component.NazcaFunctionParameters = $"length={lengthMicrometers}";
        component.WidthMicrometers = lengthMicrometers;
        component.HeightMicrometers = 10;
        component.PhysicalX = 0;
        component.PhysicalY = 0;

        AddPin(component, "a0", offsetX: 0, offsetY: 5, angleDegrees: 180);
        AddPin(component, "b0", offsetX: lengthMicrometers, offsetY: 5, angleDegrees: 0);
        return component;
    }

    private static void AddPin(
        Component comp, string name, double offsetX, double offsetY, double angleDegrees)
    {
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = name,
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = angleDegrees,
            ParentComponent = comp
        });
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}

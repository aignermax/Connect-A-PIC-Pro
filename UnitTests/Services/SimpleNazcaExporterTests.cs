using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests for the SimpleNazcaExporter segment export and component mapping.
/// </summary>
public class SimpleNazcaExporterTests
{
    [Fact]
    public void FormatSegment_StraightSegment_FirstHasCoordinates()
    {
        var segment = new StraightSegment(0, 0, 100, 0, 0);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        result.ShouldContain("nd.strt(length=100.00)");
        result.ShouldContain(".put(0.00, 0.00, 0.00)");
    }

    [Fact]
    public void FormatSegment_StraightSegment_ChainedHasEmptyPut()
    {
        var segment = new StraightSegment(100, 0, 200, 0, 0);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: false);

        result.ShouldContain("nd.strt(length=100.00)");
        result.ShouldContain(".put()");
        result.ShouldNotContain(".put(100");
    }

    [Fact]
    public void FormatSegment_BendSegment_FirstHasCoordinates()
    {
        // Sweep angle 90 → negated to -90 for Y-axis flip
        var segment = new BendSegment(50, 0, 50, 0, 90);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        result.ShouldContain("nd.bend(radius=50.00, angle=-90.00)");
        result.ShouldContain(".put(");
    }

    [Fact]
    public void FormatSegment_BendSegment_ChainedHasEmptyPut()
    {
        // Sweep angle 90 → negated to -90 for Y-axis flip
        var segment = new BendSegment(50, 0, 50, 0, 90);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: false);

        result.ShouldContain("nd.bend(radius=50.00, angle=-90.00)");
        result.ShouldEndWith(".put()");
    }

    [Fact]
    public void FormatSegment_NegativeSweepAngle_GetsNegated()
    {
        // -90 sweep → negated to +90 for Y-axis flip
        var segment = new BendSegment(50, 0, 25, 180, -90);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        result.ShouldContain("angle=90.00");
        result.ShouldContain("radius=25.00");
    }

    [Fact]
    public void AppendSegmentExport_MixedSegments_AllHaveAbsoluteCoords()
    {
        // Fix #366: all segments use absolute .put(x, y, angle), no chaining.
        // Geometrically consistent path: right → bend-down → down (Y-down editor).
        // Seg1: (0,0)→(100,0), angle=0
        // Bend: center=(100,50), R=50, startAngle=0°, sweep=+90° → CW turn in Y-down
        //   BendSegment StartPoint = center + R*(cos(startAngle-π/2), sin(startAngle-π/2)) = (100,0)
        //   BendSegment EndPoint   = (150, 50), endAngle = 90°
        // Seg2: (150,50)→(150,150), angle=90°
        var segments = new List<PathSegment>
        {
            new StraightSegment(0, 0, 100, 0, 0),
            new BendSegment(100, 50, 50, 0, 90),
            new StraightSegment(150, 50, 150, 150, 90)
        };
        var sb = new System.Text.StringBuilder();

        SimpleNazcaExporter.AppendSegmentExport(sb, segments);
        var result = sb.ToString();

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);

        // All segments must have explicit coordinates — no empty .put()
        foreach (var line in lines)
            line.Trim().ShouldNotEndWith(".put()", $"Segment must not chain: {line}");

        // Seg1: start at editor (0,0) → Nazca (0, 0)
        lines[0].ShouldContain("nd.strt(");
        lines[0].ShouldContain(".put(0.00, 0.00, 0.00)");

        // Bend: StartPoint = (100, 0) → Nazca (100, 0), angle = -0 = 0°, sweep = -90°
        lines[1].ShouldContain("nd.bend(");
        lines[1].ShouldContain(".put(100.00, 0.00,");
        lines[1].ShouldContain("angle=-90.00");

        // Seg2: start at editor (150, 50) → Nazca (150, -50), angle = -90°
        lines[2].ShouldContain("nd.strt(");
        lines[2].ShouldContain(".put(150.00, -50.00, -90.00)");
    }

    [Fact]
    public void AppendSegmentExport_SingleSegment_HasCoordinates()
    {
        // Y=20 → negated to -20
        var segments = new List<PathSegment>
        {
            new StraightSegment(10, 20, 110, 20, 0)
        };
        var sb = new System.Text.StringBuilder();

        SimpleNazcaExporter.AppendSegmentExport(sb, segments);
        var result = sb.ToString();

        result.ShouldContain(".put(10.00, -20.00, 0.00)");
    }

    [Fact]
    public void GetNazcaFunction_GratingCoupler_ReturnsGrating()
    {
        var comp = CreateComponentWithName("grating_coupler");
        var result = SimpleNazcaExporter.GetNazcaFunction(comp);
        result.ShouldBe("demo.io()");
    }

    [Fact]
    public void GetNazcaFunction_DirectionalCoupler_ReturnsMmi2x2()
    {
        var comp = CreateComponentWithName("directional_coupler");
        var result = SimpleNazcaExporter.GetNazcaFunction(comp);
        result.ShouldBe("demo.mmi2x2_dp()");
    }

    [Fact]
    public void GetNazcaFunction_Splitter_ReturnsMmi1x2()
    {
        var comp = CreateComponentWithName("splitter_1x2");
        var result = SimpleNazcaExporter.GetNazcaFunction(comp);
        result.ShouldBe("demo.mmi1x2_sh()");
    }

    [Fact]
    public void GetNazcaFunction_PhaseShifter_ReturnsEopm()
    {
        var comp = CreateComponentWithName("phase_shifter");
        var result = SimpleNazcaExporter.GetNazcaFunction(comp);
        result.ShouldBe("demo.eopm_dc(length=500)");
    }

    [Fact]
    public void GetNazcaFunction_Photodetector_ReturnsPd()
    {
        var comp = CreateComponentWithName("photodetector");
        var result = SimpleNazcaExporter.GetNazcaFunction(comp);
        result.ShouldBe("demo.pd()");
    }

    [Fact]
    public void FormatSegment_StraightWithAngle_IncludesNegatedAngle()
    {
        // angle=45 → negated to -45, Y=20 → negated to -20
        var angle = 45.0;
        var segment = new StraightSegment(10, 20, 80.71, 90.71, angle);

        var result = SimpleNazcaExporter.FormatSegment(segment, isFirst: true);

        result.ShouldContain(".put(10.00, -20.00, -45.00)");
    }

    [Fact]
    public void FormatSegment_DefaultIsFirst_HasCoordinates()
    {
        // Y=10 → negated to -10
        var segment = new StraightSegment(5, 10, 55, 10, 0);

        var result = SimpleNazcaExporter.FormatSegment(segment);

        result.ShouldContain(".put(5.00, -10.00, 0.00)");
    }

    private static Component CreateComponentWithName(string nazcaFunctionName)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: nazcaFunctionName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: nazcaFunctionName,
            rotationCounterClock: DiscreteRotation.R0
        );

        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;

        return component;
    }

    // ── ComponentGroup export tests ──────────────────────────────────────────

    [Fact]
    public void Export_ComponentGroupWithTwoChildren_ExportsBothChildren()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var group = CreateTestGroupWithTwoChildren();
        canvas.Components.Add(new ComponentViewModel(group));

        var exporter = new SimpleNazcaExporter();

        // Act
        var result = exporter.Export(canvas);

        // Assert: both children appear as comp_0 and comp_1 placements
        result.ShouldContain("comp_0 =");
        result.ShouldContain("comp_1 =");
        result.ShouldContain("splitter_1x2");
        result.ShouldContain("grating_coupler");
    }

    [Fact]
    public void Export_ComponentGroupWithFrozenPath_ExportsWaveguideSegments()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var group = CreateTestGroupWithFrozenPath();
        canvas.Components.Add(new ComponentViewModel(group));

        var exporter = new SimpleNazcaExporter();

        // Act
        var result = exporter.Export(canvas);

        // Assert: frozen waveguide is exported as nd.strt segment
        result.ShouldContain("nd.strt(");
        result.ShouldContain("# Waveguide Connections");
    }

    [Fact]
    public void Export_ComponentGroupChildren_AreNotExportedAsGroupItself()
    {
        // Arrange: a group should not appear as its own component (identifier "group")
        var canvas = new DesignCanvasViewModel();
        var group = CreateTestGroupWithTwoChildren();
        canvas.Components.Add(new ComponentViewModel(group));

        var exporter = new SimpleNazcaExporter();

        // Act
        var result = exporter.Export(canvas);

        // The group identifier itself should not be placed, only its children
        result.ShouldNotContain($"# {group.Identifier}");
    }

    [Fact]
    public void Export_NestedComponentGroup_FlattensAllDescendants()
    {
        // Arrange: outer group contains an inner group with one child
        var inner = CreateTestGroupWithTwoChildren();
        inner.GroupName = "InnerGroup";

        var outer = new ComponentGroup("OuterGroup");
        outer.PhysicalX = 0;
        outer.PhysicalY = 0;
        outer.AddChild(inner);

        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(outer));

        var exporter = new SimpleNazcaExporter();

        // Act
        var result = exporter.Export(canvas);

        // Both children of the inner group should be exported
        result.ShouldContain("comp_0 =");
        result.ShouldContain("comp_1 =");
    }

    private static ComponentGroup CreateTestGroupWithTwoChildren()
    {
        var group = new ComponentGroup("TestGroup");
        group.PhysicalX = 100;
        group.PhysicalY = 50;

        var child1 = CreateComponentWithName("splitter_1x2");
        child1.PhysicalX = 100;
        child1.PhysicalY = 50;

        var child2 = CreateComponentWithName("grating_coupler");
        child2.PhysicalX = 200;
        child2.PhysicalY = 50;

        group.AddChild(child1);
        group.AddChild(child2);

        return group;
    }

    private static ComponentGroup CreateTestGroupWithFrozenPath()
    {
        var group = new ComponentGroup("GroupWithPath");
        group.PhysicalX = 0;
        group.PhysicalY = 0;

        var child = CreateComponentWithName("splitter_1x2");
        child.PhysicalX = 50;
        child.PhysicalY = 50;
        group.AddChild(child);

        // Create a frozen path (straight segment from (50,50) to (150,50))
        var routedPath = new RoutedPath();
        routedPath.Segments.Add(new StraightSegment(50, 50, 150, 50, 0));

        var frozenPath = new FrozenWaveguidePath
        {
            Path = routedPath,
            StartPin = new PhysicalPin { Name = "b0", ParentComponent = child },
            EndPin = new PhysicalPin { Name = "a0", ParentComponent = child }
        };

        group.AddInternalPath(frozenPath);

        return group;
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

    // IsPdkFunction lives in NazcaCoordinateMapper (single source of truth, #565);
    // the exporter consumes it from there.
    [Fact]
    public void IsPdkFunction_RealPdkFunction_ReturnsTrue()
    {
        var result = NazcaCoordinateMapper.IsPdkFunction("ebeam_y_1550");
        result.ShouldBeTrue();
    }

    [Fact]
    public void IsPdkFunction_DemoPdkFunction_ReturnsFalse()
    {
        var result = NazcaCoordinateMapper.IsPdkFunction("demo_pdk.mmi1x2");
        result.ShouldBeFalse();
    }

    [Fact]
    public void IsPdkFunction_ExternalPdkWithDot_ReturnsTrue()
    {
        var result = NazcaCoordinateMapper.IsPdkFunction("siepic.gc_te1550");
        result.ShouldBeTrue();
    }

    [Fact]
    public void GetNazcaFunction_DemoPdkStraightWithLength100_ReturnsCorrectCall()
    {
        var comp = CreateComponentWithName("demo_pdk.straight");
        comp.NazcaFunctionParameters = "length=100";
        comp.WidthMicrometers = 100;
        comp.HeightMicrometers = 10;

        var result = SimpleNazcaExporter.GetNazcaFunction(comp);

        result.ShouldBe("demo_pdk_straight(length=100)");
    }

    [Fact]
    public void GetNazcaFunction_DemoPdkStraightWithoutParams_UsesComponentWidth()
    {
        var comp = CreateComponentWithName("straight_waveguide");
        comp.WidthMicrometers = 150;
        comp.HeightMicrometers = 10;

        var result = SimpleNazcaExporter.GetNazcaFunction(comp);

        result.ShouldBe("demo.shallow.strt(length=150)");
    }

    [Fact]
    public void GetNazcaFunction_UnknownComponent_UsesWidth()
    {
        var comp = CreateComponentWithName("unknown_component");
        comp.WidthMicrometers = 75;
        comp.HeightMicrometers = 25;

        var result = SimpleNazcaExporter.GetNazcaFunction(comp);

        result.ShouldBe("demo.shallow.strt(length=75)");
    }

    [Fact]
    public void GetNazcaFunction_RealPdkFunction_IncludesParameters()
    {
        var comp = CreateComponentWithName("ebeam_y_1550");
        comp.NazcaFunctionParameters = "wg_width=0.5";

        var result = SimpleNazcaExporter.GetNazcaFunction(comp);

        result.ShouldBe("ebeam_y_1550(wg_width=0.5)");
    }

    [Fact]
    public void Export_StraightWaveguide100um_ExportsCorrectLength()
    {
        // Arrange: Create a canvas with a 100µm straight waveguide
        var canvas = new DesignCanvasViewModel();
        var comp = CreateDemoPdkStraightWaveguide(100);
        var compVm = new ComponentViewModel(comp);
        canvas.Components.Add(compVm);

        // Act: Export to Nazca Python
        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Assert: Verify that the exported code includes the correct length parameter
        result.ShouldContain("demo_pdk_straight(length=100)");

        // Verify stub function is parametric
        result.ShouldContain("def demo_pdk_straight(length=100, **kwargs):");
        result.ShouldContain("nd.strt(length=length");

        // Verify component placement uses the stub
        result.ShouldContain("comp_0 = demo_pdk_straight(length=100).put(");
    }

    [Fact]
    public void Export_StraightWaveguide200um_ExportsCorrectLength()
    {
        // Arrange: Create a canvas with a 200µm straight waveguide
        var canvas = new DesignCanvasViewModel();
        var comp = CreateDemoPdkStraightWaveguide(200);
        var compVm = new ComponentViewModel(comp);
        canvas.Components.Add(compVm);

        // Act: Export to Nazca Python
        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Assert: Verify correct length
        result.ShouldContain("demo_pdk_straight(length=200)");
        result.ShouldContain("comp_0 = demo_pdk_straight(length=200).put(");
    }

    // ── Per-instance RawCode override export tests (issue #559) ───────────────

    private const string OverrideRawCode = """
        import nazca as nd

        def component():
            with nd.Cell() as C:
                nd.strt(length=42).put()
                return C
        """;

    [Fact]
    public void Export_OverriddenComponent_EmitsFactoryAndPlacesViaFactory()
    {
        // Arrange: two components, override only the first.
        var canvas = new DesignCanvasViewModel();
        var overridden = CreateComponentWithName("ebeam_y_1550");
        overridden.Identifier = "My Override Instance";
        var plain = CreateComponentWithName("splitter_1x2");
        plain.Identifier = "Plain Splitter";
        canvas.Components.Add(new ComponentViewModel(overridden));
        canvas.Components.Add(new ComponentViewModel(plain));

        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [overridden.Identifier] = new NazcaCodeOverride { RawCode = OverrideRawCode }
        };

        // Act
        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas, overrides: overrides);

        // Assert: factory definition + raw-code body present.
        var factoryName = "_ovr_My_Override_Instance";
        result.ShouldContain($"def {factoryName}():");
        result.ShouldContain("nd.strt(length=42).put()");
        result.ShouldContain("return component()");

        // Overridden instance placed via factory, WITHOUT 'org' anchor,
        // and NOT via the PDK template call.
        result.ShouldContain($"{factoryName}().put(");
        result.ShouldContain("(raw-code override)");
        result.ShouldNotContain($"{factoryName}().put('org'");
        result.ShouldNotContain("ebeam_y_1550().put");

        // No PDK stub emitted for the overridden component.
        result.ShouldNotContain("with nd.Cell(name='ebeam_y_1550')");
    }

    [Fact]
    public void Export_GdsFactoryBackendOverride_IsIgnoredByNazcaExport()
    {
        // A gdsfactory-backend override (issue #637) must NOT be embedded in the
        // Nazca script — the instance falls back to its PDK template placement.
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponentWithName("ebeam_y_1550");
        comp.Identifier = "GF Override";
        canvas.Components.Add(new ComponentViewModel(comp));

        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [comp.Identifier] = new NazcaCodeOverride
            {
                RawCode = "import gdsfactory as gf",
                Backend = OverrideBackend.GdsFactory,
            }
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        result.ShouldNotContain("_ovr_GF_Override");
        result.ShouldNotContain("import gdsfactory");
        result.ShouldContain(".put('org',");
    }

    [Fact]
    public void Export_NonOverriddenComponent_StillUsesOrgPlacement()
    {
        var canvas = new DesignCanvasViewModel();
        var overridden = CreateComponentWithName("ebeam_y_1550");
        overridden.Identifier = "Overridden";
        var plain = CreateComponentWithName("ebeam_dc_te1550");
        plain.Identifier = "Plain";
        canvas.Components.Add(new ComponentViewModel(overridden));
        canvas.Components.Add(new ComponentViewModel(plain));

        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [overridden.Identifier] = new NazcaCodeOverride { RawCode = OverrideRawCode }
        };

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas, overrides: overrides);

        // The non-overridden component keeps its normal .put('org', ...) placement.
        result.ShouldContain(".put('org',");
        result.ShouldContain("# Plain");
    }

    [Fact]
    public void Export_ConnectionTouchingOverriddenInstance_IsExportedViaPinReference()
    {
        // Arrange: two waveguides connected; override one of them.
        var canvas = new DesignCanvasViewModel();
        var compA = CreateDemoPdkStraightWaveguide(100);
        compA.Identifier = "WG A";
        var compB = CreateDemoPdkStraightWaveguide(100);
        compB.Identifier = "WG B";
        compB.PhysicalX = 200;
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        // Connect A.b0 -> B.a0
        var conn = new WaveguideConnection
        {
            StartPin = compA.PhysicalPins.First(p => p.Name == "b0"),
            EndPin = compB.PhysicalPins.First(p => p.Name == "a0")
        };
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));

        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [compB.Identifier] = new NazcaCodeOverride { RawCode = OverrideRawCode }
        };

        // Act
        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas, overrides: overrides);

        // Assert (issue #561): connection is NO LONGER skipped. The overridden endpoint
        // is wired via Nazca pin reference (its cell defines the in-app pin names); the
        // regular PDK endpoint is anchored by an absolute (x, y, angle) tuple, because a
        // PDK cell's own pin names don't match the in-app names (KeyError at run time).
        result.ShouldNotContain("# NOTE: connection to overridden instance");
        result.ShouldNotContain("skipped");
        result.ShouldContain("sbend_p2p");
        result.ShouldContain(".pin['a0']");
        result.ShouldNotContain(".pin['b0']");
        result.ShouldContain("ic.sbend_p2p((");
    }

    [Fact]
    public void Export_ConnectionBetweenTwoNonOverriddenComponents_StillUsesSegmentOrFallback()
    {
        // Non-overridden connections should not be affected by the fix.
        var canvas = new DesignCanvasViewModel();
        var compA = CreateDemoPdkStraightWaveguide(100);
        compA.Identifier = "WG A";
        var compB = CreateDemoPdkStraightWaveguide(100);
        compB.Identifier = "WG B";
        compB.PhysicalX = 200;
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        var conn = new WaveguideConnection
        {
            StartPin = compA.PhysicalPins.First(p => p.Name == "b0"),
            EndPin = compB.PhysicalPins.First(p => p.Name == "a0")
        };
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));

        // No overrides
        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Assert: connection exported normally (no NOTE comment, some connection output)
        result.ShouldNotContain("# NOTE:");
        result.ShouldContain("# Waveguide Connections");
    }

    // ── Override placement anchoring + segment export (issue #561) ────────────

    [Fact]
    public void Export_OverrideWithBboxAnchor_PlacesCellOrgAnchoredOnGridRectangle()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateDemoPdkStraightWaveguide(100);
        comp.Identifier = "Anchored Override";
        comp.PhysicalX = 100;
        comp.PhysicalY = 50;
        canvas.Components.Add(new ComponentViewModel(comp));

        var record = new NazcaCodeOverride { RawCode = OverrideRawCode };
        // Cell-internal bbox: left edge at -3, top edge at +10 (Nazca Y-up).
        record.SetOverrideGeometry(width: 45, height: 11, bboxXMin: -3, bboxYMax: 10);
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [comp.Identifier] = record
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        // org must land at (PhysicalX − XMin, −(PhysicalY + YMax)) = (103, −60) so the
        // geometry bbox sits exactly on the component's grid rectangle.
        result.ShouldContain(".put('org', 103.00, -60.00, 0)");
    }

    [Fact]
    public void Export_OverrideWithoutBboxAnchor_KeepsLegacyDefaultAnchorPlacement()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateDemoPdkStraightWaveguide(100);
        comp.Identifier = "Legacy Override";
        canvas.Components.Add(new ComponentViewModel(comp));

        // Pre-#561 record: RawCode only, no bbox anchor fields.
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [comp.Identifier] = new NazcaCodeOverride { RawCode = OverrideRawCode }
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        result.ShouldContain("(raw-code override)");
        result.ShouldNotContain("(raw-code override, bbox-anchored)");
    }

    [Fact]
    public void Export_OverriddenConnectionWithRoutedSegments_ExportsSegmentsNotP2p()
    {
        var canvas = new DesignCanvasViewModel();
        var compA = CreateDemoPdkStraightWaveguide(100);
        compA.Identifier = "WG A";
        var compB = CreateDemoPdkStraightWaveguide(100);
        compB.Identifier = "WG B";
        compB.PhysicalX = 200;
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        var conn = new WaveguideConnection
        {
            StartPin = compA.PhysicalPins.First(p => p.Name == "b0"),
            EndPin = compB.PhysicalPins.First(p => p.Name == "a0")
        };
        var cachedPath = new RoutedPath();
        cachedPath.Segments.Add(new StraightSegment(100, 5, 200, 5, 0));
        conn.RestoreCachedPath(cachedPath);
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));

        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [compB.Identifier] = new NazcaCodeOverride { RawCode = OverrideRawCode }
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        // The UI-routed geometry must be exported 1:1 — no free-form p2p interconnect
        // with Nazca's own (much larger) default bend radii. (Exact endpoint maths is
        // covered by the two coordinate tests below; this fixture's plain component is
        // uncalibrated, so its legacy pin position is not meaningful to assert on.)
        result.ShouldNotContain("sbend_p2p");
        result.ShouldContain("nd.strt(length=");
    }

    [Fact]
    public void Export_SegmentStartingAtOverriddenPin_UsesPlainNazcaConversion()
    {
        // A bbox-anchored override cell puts its pins exactly at the plain app→Nazca
        // conversion (x, −y). The legacy GetAbsoluteNazcaPosition calibration math is
        // only valid for PDK cells — applied to an override pin it shifts the whole
        // waveguide in Y (manual finding: 109.505 µm offset in KLayout).
        var canvas = new DesignCanvasViewModel();
        var compA = CreateDemoPdkStraightWaveguide(100);   // overridden, at (200, 0)
        compA.Identifier = "WG Override";
        compA.PhysicalX = 200;
        var compB = CreateDemoPdkStraightWaveguide(100);
        compB.Identifier = "WG Plain";
        compB.PhysicalX = 400;
        canvas.Components.Add(new ComponentViewModel(compA));
        canvas.Components.Add(new ComponentViewModel(compB));

        // Connection STARTS at the overridden component's 'b0' (app world (300, 5)).
        var conn = new WaveguideConnection
        {
            StartPin = compA.PhysicalPins.First(p => p.Name == "b0"),
            EndPin = compB.PhysicalPins.First(p => p.Name == "a0")
        };
        var singleStraight = new RoutedPath();
        singleStraight.Segments.Add(new StraightSegment(300, 5, 400, 5, 0));
        conn.RestoreCachedPath(singleStraight);
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));

        var record = new NazcaCodeOverride { RawCode = OverrideRawCode };
        record.SetOverrideGeometry(width: 100, height: 10, bboxXMin: 0, bboxYMax: 10);
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [compA.Identifier] = record
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        // App pin (300, 5) → Nazca (300, −5). The legacy math would emit +5.00 here.
        result.ShouldContain(".put(300.00, -5.00");
    }

    [Fact]
    public void Export_MultiSegmentFromOverriddenPin_AppliesNoLegacyOffset()
    {
        var canvas = new DesignCanvasViewModel();
        var compA = CreateDemoPdkStraightWaveguide(100);
        compA.Identifier = "WG Override Multi";
        compA.PhysicalX = 200;
        canvas.Components.Add(new ComponentViewModel(compA));
        var compB = CreateDemoPdkStraightWaveguide(100);
        compB.Identifier = "WG Plain Multi";
        compB.PhysicalX = 500;
        canvas.Components.Add(new ComponentViewModel(compB));

        var conn = new WaveguideConnection
        {
            StartPin = compA.PhysicalPins.First(p => p.Name == "b0"),
            EndPin = compB.PhysicalPins.First(p => p.Name == "a0")
        };
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(300, 5, 400, 5, 0));
        path.Segments.Add(new StraightSegment(400, 5, 500, 5, 0));
        conn.RestoreCachedPath(path);
        canvas.Connections.Add(new WaveguideConnectionViewModel(conn));

        var record = new NazcaCodeOverride { RawCode = OverrideRawCode };
        record.SetOverrideGeometry(width: 100, height: 10, bboxXMin: 0, bboxYMax: 10);
        var overrides = new Dictionary<string, NazcaCodeOverride>
        {
            [compA.Identifier] = record
        };

        var result = new SimpleNazcaExporter().Export(canvas, overrides: overrides);

        // First segment must start at the plain conversion of the app start point
        // (300, 5) → (300, −5); a legacy start-pin offset would shift every segment.
        result.ShouldContain("300.00, -5.00");
        result.ShouldNotContain("300.00, 5.00");
    }

    [Fact]
    public void Export_DefaultFlags_OmitsVerificationEpilog()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CreateDemoPdkStraightWaveguide(100)));

        var result = new SimpleNazcaExporter().Export(canvas);

        // The verification epilog is opt-in (issue #565): a regular export must stay
        // a plain fab script without introspection side effects.
        result.ShouldNotContain("Alignment verification");
        result.ShouldNotContain("_verify_instances");
        result.ShouldNotContain(".pins.json");
    }

    [Fact]
    public void Export_WithEmitVerification_AppendsPinEpilogAfterGdsExport()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(CreateDemoPdkStraightWaveguide(100)));

        var result = new SimpleNazcaExporter().Export(canvas, emitVerification: true);

        // The registry bridges the function-local comp_N variables to the module-level
        // epilog, which dumps every instance's TRUE nazca pin positions to .pins.json.
        result.ShouldContain("_verify_instances = [('comp_0', comp_0)]");
        result.ShouldContain("# --- Alignment verification (machine-readable) ---");
        result.ShouldContain("_pin.xya()");
        result.ShouldContain(".pins.json");
        result.IndexOf("nd.export_gds", StringComparison.Ordinal).ShouldBeLessThan(
            result.IndexOf("# --- Alignment verification", StringComparison.Ordinal),
            "the epilog must run after the GDS was written");
    }

    private static Component CreateDemoPdkStraightWaveguide(double lengthMicrometers)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "demo_pdk.straight",
            nazcaFunctionParams: $"length={lengthMicrometers}",
            parts: parts,
            typeNumber: 0,
            identifier: $"Straight Waveguide {lengthMicrometers}µm",
            rotationCounterClock: DiscreteRotation.R0
        );

        component.WidthMicrometers = lengthMicrometers;
        component.HeightMicrometers = 10;
        component.PhysicalX = 0;
        component.PhysicalY = 0;

        // Add physical pins (input at x=0, output at x=length)
        var inputPin = new PhysicalPin
        {
            Name = "a0",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
            ParentComponent = component
        };

        var outputPin = new PhysicalPin
        {
            Name = "b0",
            OffsetXMicrometers = lengthMicrometers,
            OffsetYMicrometers = 5,
            AngleDegrees = 0,
            ParentComponent = component
        };

        component.PhysicalPins.Add(inputPin);
        component.PhysicalPins.Add(outputPin);

        return component;
    }
}

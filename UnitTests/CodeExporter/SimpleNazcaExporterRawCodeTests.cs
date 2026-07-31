using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.CodeExporter;

/// <summary>
/// Tests for the raw-code inlining of <see cref="SimpleNazcaExporter"/> (issue #808,
/// export step): a placed component whose library template carries nazca-backend
/// <c>RawCode</c> (GDS imports via <c>nd.load_gds</c>, custom Python cells) exports
/// its REAL geometry through an aligned wrapper cell instead of falling through to
/// the demofab heuristics / a box stub. Covers the once-per-template emission, the
/// pin/label mapping, rotation handling, the gdsfactory-backend exclusion, the
/// no-library legacy path, and the missing-.gds fallback (box stub + warning).
/// </summary>
public class SimpleNazcaExporterRawCodeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-rawcode-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // ── Inlining ─────────────────────────────────────────────────────────────

    [Fact]
    public void Export_RawCodeComponent_InlinesModuleOnce_PerTemplate_WithAlignedWrapperAndPins()
    {
        var gdsPath = WriteGds("circuit.gds");
        var template = GdsTemplate(gdsPath);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_2", x: 20, y: 0)));

        var script = new SimpleNazcaExporter().Export(canvas, library: new[] { template });

        // The raw module is emitted exactly once although the template is placed twice.
        CountOccurrences(script, "nd.load_gds(filename=").ShouldBe(1);
        script.ShouldContain($"\"{Escaped(gdsPath)}\"");

        // Aligned wrapper cell: the raw cell is re-anchored to the bbox bottom-left.
        script.ShouldContain("with nd.Cell(name='component_wgA') as _component_wgA_cell:");
        script.ShouldContain("_raw_component_wgA = component() if callable(globals().get('component')) else cell");
        script.ShouldContain("_raw_component_wgA.put(-_bb[0], -_bb[1])");
        script.ShouldContain("def component_wgA(**kwargs):");

        // Pins follow the stub mapping: legacy anchor (0, H=4) → local (x, 4 - y), angle negated.
        script.ShouldContain("nd.Pin('in').put(0.00, 2.00, -180)");
        script.ShouldContain("nd.Pin('out').put(10.00, 2.00, 0)");
        script.ShouldContain("nd.Annotation(text='in', layer=(1, 10)).put(0.00, 2.00)");
        script.ShouldContain("nd.Annotation(text='out', layer=(1, 10)).put(10.00, 2.00)");

        // Both placements call the wrapper org-anchored; no demofab heuristic is involved.
        script.ShouldContain("comp_0 = component_wgA().put('org', 0.00, -4.00, 0)");
        script.ShouldContain("comp_1 = component_wgA().put('org', 20.00, -4.00, 0)");
        script.ShouldNotContain("demo.shallow.strt");
        script.ShouldNotContain("Auto-generated stub");
    }

    [Fact]
    public void Export_TwoRawCodeTemplates_EachModuleEmittedOnce_WrappersCaptureTheirOwnComponent()
    {
        var gdsPath = WriteGds("circuit.gds");
        var templateA = GdsTemplate(gdsPath);
        var templateB = GdsTemplate(gdsPath, name: "wgB", cellName: "wgB");
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgB_1", x: 20, y: 0, funcName: "nazca_wgb")));

        var script = new SimpleNazcaExporter().Export(canvas, library: new[] { templateA, templateB });

        // Each raw module is emitted and built before the next one redefines component().
        CountOccurrences(script, "nd.load_gds(filename=").ShouldBe(2);
        var indexA = script.IndexOf("_raw_component_wgA = component()", StringComparison.Ordinal);
        var indexBModule = script.IndexOf("cellname=\"wgB\"", StringComparison.Ordinal);
        var indexB = script.IndexOf("_raw_component_wgB = component()", StringComparison.Ordinal);
        indexA.ShouldBeGreaterThanOrEqualTo(0);
        indexBModule.ShouldBeGreaterThan(indexA);
        indexB.ShouldBeGreaterThan(indexBModule);
    }

    [Fact]
    public void Export_RotatedRawCodeComponent_WrapperKeepsUnrotatedPins_PlacementNegatesRotation()
    {
        var gdsPath = WriteGds("circuit.gds");
        var template = GdsTemplate(gdsPath);
        // One CCW quarter turn applied for real: RotationDegrees 90, live dimensions swapped,
        // live pin offsets rotated — (x, y) → (H0 - y, x) on a 10×4 box: in (0,2)→(2,0),
        // out (10,2)→(2,10). The wrapper must invert this back to the unrotated frame.
        var comp = GdsComponent("wgA_1", x: 100, y: 46);
        comp.RotationDegrees = 90;
        comp.WidthMicrometers = 4;
        comp.HeightMicrometers = 10;
        comp.PhysicalPins.Clear();
        AddPin(comp, "in", offsetX: 2, offsetY: 0, angleDegrees: 180);
        AddPin(comp, "out", offsetX: 2, offsetY: 10, angleDegrees: 0);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(comp));

        var script = new SimpleNazcaExporter().Export(canvas, library: new[] { template });

        // The cell is rotation-independent: pins use the UNROTATED offsets (anchor (0, H0=4)),
        // the placement applies the negated rotation instead of baking it into the cell.
        script.ShouldContain("nd.Pin('in').put(0.00, 2.00, -180)");
        script.ShouldContain("nd.Pin('out').put(10.00, 2.00, 0)");
        script.ShouldContain("comp_0 = component_wgA().put('org', 100.00, -46.00, -90)");
    }

    [Fact]
    public void Export_GdsFactoryBackendRawCode_IsNotInlined()
    {
        var gdsPath = WriteGds("circuit.gds");
        var template = GdsTemplate(gdsPath);
        template.RawCodeBackend = "gdsfactory";
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));

        var script = new SimpleNazcaExporter().Export(canvas, library: new[] { template });

        script.ShouldNotContain("component_wgA");
        CountOccurrences(script, "nd.load_gds(filename=").ShouldBe(0);
    }

    [Fact]
    public void Export_NoLibrary_KeepsLegacyHeuristicBehavior()
    {
        var gdsPath = WriteGds("circuit.gds");
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldNotContain("component_wgA");
        CountOccurrences(script, "nd.load_gds(filename=").ShouldBe(0);
        script.ShouldContain("demo.shallow.strt"); // pre-inlining fallback for unknown names
    }

    // ── Missing-source fallback ───────────────────────────────────────────────

    [Fact]
    public void Export_DeletedGdsFile_FallsBackToBoxStub_AndCollectsWarning()
    {
        var missingPath = Path.Combine(_root, "deleted.gds"); // never written
        var template = GdsTemplate(missingPath);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));

        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, library: new[] { template }, exportWarnings: warnings);

        // Box stub under the wrapper's function name, anchored like the placement expects
        // (legacy anchor (0, H=4): box (0,0)..(10,4), pins at (x, 4-y)) — NOT the raw module.
        CountOccurrences(script, "nd.load_gds(filename=").ShouldBe(0);
        script.ShouldContain("with nd.Cell(name='component_wgA') as _component_wgA_cell:");
        script.ShouldContain(
            "nd.Polygon(points=[(0.00,0.00),(10.00,0.00),(10.00,4.00),(0.00,4.00)], layer=1)");
        script.ShouldContain("nd.Pin('in').put(0.00, 2.00, -180)");
        script.ShouldContain("comp_0 = component_wgA().put('org', 0.00, -4.00, 0)");

        var warning = warnings.ShouldHaveSingleItem();
        warning.ShouldContain("wgA");
        warning.ShouldContain(missingPath);
        warning.ShouldContain("placeholder box");
    }

    [Fact]
    public void Export_ExistingGdsFile_NoFallbackWarning()
    {
        var gdsPath = WriteGds("circuit.gds");
        var template = GdsTemplate(gdsPath);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));

        var warnings = new List<string>();
        new SimpleNazcaExporter().Export(canvas, library: new[] { template }, exportWarnings: warnings);

        warnings.ShouldBeEmpty();
    }

    // ── Partial export ────────────────────────────────────────────────────────

    [Fact]
    public void ExportPartial_RawCodeComponent_InlinedOnlyWhenIncluded()
    {
        var gdsPath = WriteGds("circuit.gds");
        var template = GdsTemplate(gdsPath);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(GdsComponent("wgA_1", x: 0, y: 0)));

        var included = new SimpleNazcaExporter().ExportPartial(
            canvas, _ => true, "Partial", library: new[] { template });
        included.ShouldContain("with nd.Cell(name='component_wgA') as _component_wgA_cell:");
        included.ShouldContain("comp_0 = component_wgA().put('org', 0.00, -4.00, 0)");

        var excluded = new SimpleNazcaExporter().ExportPartial(
            canvas, _ => false, "Partial", library: new[] { template });
        excluded.ShouldNotContain("component_wgA");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private string WriteGds(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); // existence is all the plan checks
        return path;
    }

    private static string Escaped(string path) =>
        path.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// A template shaped like a GDS-imported component: no explicit Nazca function
    /// (the component then carries the synthesized <c>nazca_&lt;name&gt;</c>), a
    /// load_gds raw-code snippet, and the two pins of the 10×4 µm waveguide fixture.
    /// </summary>
    private static ComponentTemplate GdsTemplate(string gdsPath, string name = "wgA", string cellName = "wgA") =>
        new()
        {
            Name = name,
            PdkSource = "GDS Import - circuit",
            WidthMicrometers = 10,
            HeightMicrometers = 4,
            PinDefinitions = new[]
            {
                new PinDefinition("in", 0, 2, 180),
                new PinDefinition("out", 10, 2, 0),
            },
            RawCode =
                "import nazca as nd\n" +
                "\n" +
                "def component():\n" +
                $"    with nd.Cell(name=\"{cellName}_aligned\") as cell:\n" +
                $"        _loaded = nd.load_gds(filename=\"{Escaped(gdsPath)}\", cellname=\"{cellName}\", topcellsonly=False)\n" +
                "        _bb = _loaded.bbox\n" +
                "        _loaded.put(-_bb[0], -_bb[1])\n" +
                "    return cell\n",
            RawCodeBackend = "nazca",
        };

    /// <summary>A placed component of <see cref="GdsTemplate"/> (10×4 µm at (x, y)).</summary>
    private static Component GdsComponent(
        string identifier, double x, double y, string funcName = "nazca_wga")
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var comp = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: funcName,
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0)
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 10,
            HeightMicrometers = 4,
        };
        AddPin(comp, "in", offsetX: 0, offsetY: 2, angleDegrees: 180);
        AddPin(comp, "out", offsetX: 10, offsetY: 2, angleDegrees: 0);
        return comp;
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

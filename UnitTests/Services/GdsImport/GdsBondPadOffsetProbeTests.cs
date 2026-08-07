using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Export-geometry measurement of the user's PR #811 report: after a Whole-Layout
/// GDS export of his MZI, the metal bond pads look OFFSET — the metal routes only
/// touch the pads at a corner. Three of his four pads are rotated 180°.
/// <para>
/// Exports <see cref="GdsMziElectricalFixture"/> twice — normally (the klayout
/// SiEPIC upgrade swaps the pad stub for the real foundry cell when the PDK is
/// installed) and forced-stub (klayout/siepic imports poisoned) — then measures,
/// per pad and file: the flattened pad polygon bbox vs the app box, the 'elec'
/// (1,10) label vs the app model pin (<see cref="PhysicalPin.GetAbsolutePosition"/>,
/// Y-flipped), and the nearest top-cell metal-route polygon on (11,0).
/// </para>
/// <para>
/// Measured verdict (pinned by this test): the stub path is exact for all four pads
/// (bbox = app box, label = pin, route endpoint = pin, all within F2 rounding) —
/// <c>AppendStandardComponentStub</c>/<c>NazcaCoordinateMapper.GetStubAnchor</c>/
/// <c>NazcaPinLabelWrapperWriter</c> were never the bug. The offset appeared only
/// after the klayout upgrade: <c>SiepicCellUpgradeWriter</c>'s swap
/// (<c>_stub.copy_tree(_real)</c>) copied the real cell's ORIGIN-CENTRED geometry
/// (pad centre at the cell origin, ±50 µm) into the stub frame whose origin sits
/// at the box LEFT-EDGE MIDDLE (calibrated nazcaOriginOffset (0, 50)) — an in-cell
/// (−50, 0) µm shift that the instance rotation mapped to −50 µm X at 0° and
/// +50 µm X at 180°. The swap's <c>shapes().clear()</c> also deleted the 'elec'
/// (1,10) label; the real cell brings only SiEPIC m_pin markers on (1,11).
/// FIXED (#811): the swap now re-anchors the copied content into the stub frame
/// (centroid of the real cell's (1,10)/(1,11) pin-marker texts onto the centroid
/// of the stub's pin labels; bbox-centre fallback) and re-emits the stub's (1,10)
/// pin labels — the UPGRADED assertions below pin the fixed behaviour: ≈0 offsets,
/// label present, pin on the pad.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsBondPadOffsetProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bondpad-probe-" + Guid.NewGuid().ToString("N"));

    public GdsBondPadOffsetProbeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record ExpectedPad(
        string Identifier,
        double RotationDegrees,
        double BoxMinX, double BoxMinY, double BoxMaxX, double BoxMaxY, // nazca (Y-up) app box
        double PinX, double PinY,                                       // nazca expected pin
        double PutX, double PutY);                                      // nazca cell put position

    [SkippableFact]
    public async Task Probe_BondPadAlignment()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "no nazca python");

        var canvas = GdsMziElectricalFixture.BuildMziCanvas();

        // Expected truth from the app model (export's Y-flip applied: nazca = (x, -y)).
        var expected = new List<ExpectedPad>();
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (!comp.Identifier.StartsWith("Bond_Pad", StringComparison.Ordinal))
                continue;
            var pin = comp.PhysicalPins.First(p => p.Name == "elec");
            var (appX, appY) = pin.GetAbsolutePosition();
            var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
            expected.Add(new ExpectedPad(
                comp.Identifier,
                comp.RotationDegrees,
                comp.PhysicalX, -comp.PhysicalY - comp.HeightMicrometers,
                comp.PhysicalX + comp.WidthMicrometers, -comp.PhysicalY,
                appX, -appY,
                placement.X, placement.Y));
        }
        expected.Count.ShouldBe(4);

        var skipped = new List<string>();
        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, skippedConnections: skipped, exportWarnings: warnings);
        _output.WriteLine($"skipped=[{string.Join(";", skipped)}] warnings=[{string.Join(";", warnings)}]");

        Directory.CreateDirectory(_root);
        var scriptPath = Path.Combine(_root, "mzi.py");
        await File.WriteAllTextAsync(scriptPath, script);

        // Normal run — the klayout SiEPIC upgrade swaps stub boxes for real foundry
        // geometry when klayout + siepic_ebeam_pdk are installed.
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, _root, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca run failed: {run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue();
        var upgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);
        _output.WriteLine($"normal run: upgraded={upgraded}");
        var upgradedCopy = Path.Combine(_root, "mzi_upgraded.gds");
        File.Copy(gdsPath, upgradedCopy, overwrite: true);

        // Forced STUB scenario: same script, klayout/siepic imports poisoned.
        var stubRunner = Path.Combine(_root, "mzi_stub.py");
        await File.WriteAllTextAsync(stubRunner,
            "import sys, runpy\n" +
            "sys.modules['klayout'] = None\n" +
            "sys.modules['klayout.db'] = None\n" +
            "sys.modules['siepic_ebeam_pdk'] = None\n" +
            $"sys.argv = [r'{scriptPath}']\n" +
            $"runpy.run_path(r'{scriptPath}', run_name='__main__')\n");
        var stubRun = await SiepicRealGeometryExportTests.RunPythonAsync(python, _root, stubRunner);
        stubRun.ExitCode.ShouldBe(0, $"stub run failed: {stubRun.StdErr}");
        var stubCopy = Path.Combine(_root, "mzi_stub.gds");
        File.Move(gdsPath, stubCopy, overwrite: true);

        await Analyze("STUB", stubCopy, expected, upgraded: false);
        if (upgraded)
            await Analyze("UPGRADED", upgradedCopy, expected, upgraded: true);
    }

    /// <summary>
    /// The re-anchor translation happens in the CELL frame, so it must hold for a
    /// multi-pin SiEPIC cell at any instance rotation. The 4-port crossing is the
    /// stress case: its SiEPIC pin markers are named opt*/pin* (not the app's
    /// 'port N'), so no name matching is possible, and its calibrated stub frame
    /// already coincides with the real cell's frame — the test guards both that
    /// re-anchoring leaves an aligned cell aligned (translation ≈0) and that the
    /// real cell's own (1,10) pin texts are replaced, not duplicated.
    /// </summary>
    [SkippableFact]
    public async Task Probe_CrossingAlignment()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "no nazca python");

        var template = TestPdkLoader.LoadAllTemplates()
            .First(t => t.Name == "Crossing 4-Port" && t.PdkSource == "SiEPIC EBeam PDK");
        var canvas = new DesignCanvasViewModel();
        var expected = new List<(string Id, double BoxCx, double BoxCy, (double X, double Y)[] Pins, double PutX, double PutY)>();
        foreach (var (id, x, y, rot) in new[]
        {
            ("crossing_0deg", 200.0, 200.0, 0.0),
            ("crossing_90deg", 400.0, 200.0, 90.0),
            ("crossing_180deg", 600.0, 200.0, 180.0),
        })
        {
            var comp = ComponentTemplates.CreateFromTemplate(template, x, y);
            comp.Identifier = id;
            // Rotate like the app's RotateComponentCommand (90° CCW about the box
            // centre, pin offsets included) — same precedent as GdsMziElectricalFixture.
            for (var q = (int)Math.Round(rot / 90.0); q > 0; q--)
                CAP.Avalonia.Commands.RotateComponentCommand.ApplyModelRotation90(comp);
            canvas.AddComponent(comp, template.Name, template.PdkSource);
            var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
            expected.Add((id,
                comp.PhysicalX + comp.WidthMicrometers / 2, -comp.PhysicalY - comp.HeightMicrometers / 2,
                comp.PhysicalPins.Select(p => { var (ax, ay) = p.GetAbsolutePosition(); return (ax, -ay); }).ToArray(),
                placement.X, placement.Y));
        }
        expected.Count.ShouldBe(3);

        var script = new SimpleNazcaExporter().Export(canvas);
        Directory.CreateDirectory(_root);
        var scriptPath = Path.Combine(_root, "crossings.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, _root, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca run failed: {run.StdErr}");
        Skip.If(!run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal),
            "this environment has no klayout/siepic_ebeam_pdk — nothing to re-anchor");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        var flattener = new GdsCellFlattener(library);
        var instances = flattener.GetInstanceTree("ConnectAPIC_Design")
            .Where(i => i.CellName.StartsWith("ebeam_crossing4", StringComparison.Ordinal))
            .ToList();
        instances.Count.ShouldBe(3);
        var cellFlat = flattener.Flatten(instances.First().CellName);

        foreach (var exp in expected)
        {
            var inst = instances
                .OrderBy(i => Dist(i.Offset.X, i.Offset.Y, exp.PutX, exp.PutY))
                .First();
            var polys = cellFlat.Polygons
                .Select(p => p.Points
                    .Select(q => { var t = ApplyInstance(inst, q.X, q.Y); return new GdsPoint(t.X, t.Y); })
                    .ToList())
                .ToList();
            var labels = cellFlat.Texts
                .Where(t => t.Layer == 1 && t.TextType == 10)
                .Select(t => (t.Text, Pos: ApplyInstance(inst, t.Position.X, t.Position.Y)))
                .ToList();
            var cx = (polys.SelectMany(p => p).Min(q => q.X) + polys.SelectMany(p => p).Max(q => q.X)) / 2;
            var cy = (polys.SelectMany(p => p).Min(q => q.Y) + polys.SelectMany(p => p).Max(q => q.Y)) / 2;
            _output.WriteLine(
                $"── {exp.Id}: bbox-centre offset ({cx - exp.BoxCx:+0.000;-0.000;0.000}, {cy - exp.BoxCy:+0.000;-0.000;0.000}) µm, " +
                $"labels [{string.Join(", ", labels.Select(l => l.Text))}]");

            Math.Abs(cx - exp.BoxCx).ShouldBeLessThan(0.02,
                $"{exp.Id}: the re-anchored crossing sits on the app box");
            Math.Abs(cy - exp.BoxCy).ShouldBeLessThan(0.02);
            labels.Select(l => l.Text).ShouldBe(
                new[] { "port 1", "port 2", "port 3", "port 4" }, ignoreOrder: true,
                $"{exp.Id}: exactly the app's pin labels survive — the real cell's opt* texts are replaced, not doubled");
            foreach (var pin in exp.Pins)
            {
                labels.Min(l => Dist(l.Pos.X, l.Pos.Y, pin.X, pin.Y)).ShouldBeLessThan(0.02,
                    $"{exp.Id}: a pin label sits on the app pin ({pin.X:F2}, {pin.Y:F2})");
                polys.Min(poly => PointToPolygonDistance(pin.X, pin.Y, poly)).ShouldBeLessThan(0.02,
                    $"{exp.Id}: the app pin ({pin.X:F2}, {pin.Y:F2}) lies on the crossing geometry");
            }
        }
    }

    private async Task Analyze(string label, string gdsPath, List<ExpectedPad> expected, bool upgraded)
    {
        _output.WriteLine($"════════ SCENARIO {label} ════════");
        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);

        var flattener = new GdsCellFlattener(library);
        var top = library.Cells["ConnectAPIC_Design"];

        var topMetal = top.Elements.OfType<GdsPolygon>()
            .Where(p => p.Layer == 11 && p.DataType == 0).ToList();
        topMetal.ShouldNotBeEmpty("the four metal routes are top-cell polygons on (11,0)");

        var instances = flattener.GetInstanceTree("ConnectAPIC_Design")
            .Where(i => i.CellName.StartsWith("ebeam_BondPad", StringComparison.Ordinal))
            .ToList();
        instances.Count.ShouldBe(4);

        // Cell-local content of the pad cell (stub box or swapped real geometry).
        var padFlat = flattener.Flatten(instances.First().CellName);
        _output.WriteLine($"pad cell local content: {padFlat.Polygons.Count} polygons " +
            $"on [{string.Join(", ", padFlat.Polygons.GroupBy(p => (p.Layer, p.DataType)).Select(g => $"{g.Key}×{g.Count()}"))}], " +
            $"{padFlat.Texts.Count} texts [{string.Join(", ", padFlat.Texts.Select(t => $"'{t.Text}' L{t.Layer}/{t.TextType}"))}]");

        foreach (var pad in expected.OrderBy(p => p.Identifier))
        {
            var inst = instances
                .OrderBy(i => Dist(i.Offset.X, i.Offset.Y, pad.PutX, pad.PutY))
                .First();
            inst.Reflected.ShouldBeFalse();
            inst.Magnification.ShouldBe(1.0);

            // Transform the pad cell content into top-cell (nazca) space.
            var polys = padFlat.Polygons
                .Select(p => p.Points
                    .Select(q => { var t = ApplyInstance(inst, q.X, q.Y); return new GdsPoint(t.X, t.Y); })
                    .ToList())
                .ToList();
            polys.ShouldNotBeEmpty();
            var texts = padFlat.Texts
                .Select(t => (t.Text, Pos: ApplyInstance(inst, t.Position.X, t.Position.Y), t.Layer, t.TextType))
                .ToList();

            var minX = polys.SelectMany(p => p).Min(q => q.X);
            var maxX = polys.SelectMany(p => p).Max(q => q.X);
            var minY = polys.SelectMany(p => p).Min(q => q.Y);
            var maxY = polys.SelectMany(p => p).Max(q => q.Y);
            var offX = (minX + maxX) / 2 - (pad.BoxMinX + pad.BoxMaxX) / 2;
            var offY = (minY + maxY) / 2 - (pad.BoxMinY + pad.BoxMaxY) / 2;

            var elec = texts.Where(t => t.Text == "elec").ToList();
            var pinToPad = polys.Min(poly => PointToPolygonDistance(pad.PinX, pad.PinY, poly));
            var pinToMetal = topMetal.Min(m => PointToPolygonDistance(pad.PinX, pad.PinY, m.Points));
            var nearestMetalVertex = topMetal.SelectMany(m => m.Points)
                .Select(v => (X: v.X, Y: v.Y, D: Dist(v.X, v.Y, pad.PinX, pad.PinY)))
                .OrderBy(t => t.D)
                .First();

            _output.WriteLine(
                $"── {pad.Identifier} rot={pad.RotationDegrees:F0}\n" +
                $"   app box (nazca): X[{pad.BoxMinX:F3}..{pad.BoxMaxX:F3}] Y[{pad.BoxMinY:F3}..{pad.BoxMaxY:F3}]\n" +
                $"   pad bbox (GDS) : X[{minX:F3}..{maxX:F3}] Y[{minY:F3}..{maxY:F3}]\n" +
                $"   bbox-centre offset vs app box: ({offX:+0.000;-0.000;0.000}, {offY:+0.000;-0.000;0.000}) µm\n" +
                $"   expected pin (nazca): ({pad.PinX:F3}, {pad.PinY:F3}); 'elec' label: " +
                (elec.Count == 0 ? "ABSENT" : string.Join(" | ", elec.Select(e => $"({e.Pos.X:F3},{e.Pos.Y:F3}) d={Dist(e.Pos.X, e.Pos.Y, pad.PinX, pad.PinY):F3}µm"))) + "\n" +
                $"   pin → pad polygon: {pinToPad:F3} µm; pin → nearest metal: {pinToMetal:F3} µm " +
                $"(nearest vertex ({nearestMetalVertex.X:F3},{nearestMetalVertex.Y:F3}) d={nearestMetalVertex.D:F3})");

            // The metal route must ALWAYS end exactly on the app pin (both scenarios) —
            // it is emitted straight from the routed path in app coordinates.
            pinToMetal.ShouldBeLessThan(0.01,
                $"{label} {pad.Identifier}: the metal route endpoint sits on the app pin");

            if (!upgraded)
            {
                // Stub scenario: everything consistent — box, label, pin, route.
                Math.Abs(offX).ShouldBeLessThan(0.01, $"{label} {pad.Identifier}: stub box on the app box");
                Math.Abs(offY).ShouldBeLessThan(0.01);
                elec.ShouldHaveSingleItem($"{label} {pad.Identifier}: the stub labels its elec pin");
                Dist(elec[0].Pos.X, elec[0].Pos.Y, pad.PinX, pad.PinY).ShouldBeLessThan(0.01,
                    $"{label} {pad.Identifier}: the elec label sits on the app pin");
                pinToPad.ShouldBeLessThan(0.01,
                    $"{label} {pad.Identifier}: the pin lies on the stub pad edge");
            }
            else
            {
                // UPGRADED scenario — the fix (#811): the swap re-anchors the real
                // cell into the stub frame (pin-marker centroid match), so the pad
                // lands exactly on the app box at ANY instance rotation, and the
                // restored (1,10) label keeps the pin re-importable. The real
                // cell's m_pin marker paths extend 0.1 µm past the 100 µm pad on
                // every side, so the bbox stays centred — the offsets are ≈0.
                Math.Abs(offX).ShouldBeLessThan(0.02,
                    $"{label} {pad.Identifier}: the re-anchored pad sits on the app box");
                Math.Abs(offY).ShouldBeLessThan(0.02);
                var label0 = elec.ShouldHaveSingleItem(
                    $"{label} {pad.Identifier}: the swap keeps the stub's (1,10) elec label");
                Dist(label0.Pos.X, label0.Pos.Y, pad.PinX, pad.PinY).ShouldBeLessThan(0.02,
                    $"{label} {pad.Identifier}: the elec label sits on the app pin");
                pinToPad.ShouldBeLessThan(0.02,
                    $"{label} {pad.Identifier}: the pin lies inside the re-anchored pad");
            }
        }
    }

    private static (double X, double Y) ApplyInstance(GdsInstance inst, double x, double y)
    {
        // GDS SREF semantics (mirrors GdsTransform.FromReference): magnification and
        // X-axis reflection first, then CCW rotation, then translation.
        var rad = inst.AngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var m = inst.Magnification;
        var ySign = inst.Reflected ? -1.0 : 1.0;
        return (
            cos * m * x - sin * ySign * m * y + inst.Offset.X,
            sin * m * x + cos * ySign * m * y + inst.Offset.Y);
    }

    private static double Dist(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

    private static double PointToPolygonDistance(double px, double py, IReadOnlyList<GdsPoint> poly)
    {
        if (PointInPolygon(px, py, poly))
            return 0;
        var best = double.MaxValue;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            best = Math.Min(best, PointToSegmentDistance(px, py, a.X, a.Y, b.X, b.Y));
        }
        return best;
    }

    private static double PointToSegmentDistance(
        double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        var t = lenSq == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
        return Dist(px, py, ax + t * dx, ay + t * dy);
    }

    private static bool PointInPolygon(double px, double py, IReadOnlyList<GdsPoint> poly)
    {
        // Even-odd ray cast along +X; boundary points count as inside.
        var inside = false;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if (PointToSegmentDistance(px, py, a.X, a.Y, b.X, b.Y) < 1e-9)
                return true;
            if ((a.Y > py) != (b.Y > py))
            {
                var xCross = a.X + (py - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (xCross > px)
                    inside = !inside;
            }
        }
        return inside;
    }
}

using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// The full circle of issue #808, proven against the REAL nazca engine: a GDS
/// file is imported (components registered with their <c>nd.load_gds</c> raw
/// code), placed on the canvas through the real placement executor, exported via
/// <see cref="SimpleNazcaExporter"/> with raw-code inlining, and the generated
/// script is RUN with python+nazca. The produced GDS is then read back with our
/// own <see cref="GdsReader"/>/<see cref="GdsCellFlattener"/> and must contain
/// the leaf cell's REAL geometry (the 0.5 µm core stripe — not the 10×4 µm box
/// a stub would draw) plus the pin labels as TEXT records on (1, 10), at the
/// positions the import placed them. Skipped cleanly when no python with nazca
/// is available (mirrors <c>SiepicRealGeometryExportTests</c>' environment guard).
/// </summary>
[Trait("Category", "Slow")]
public class GdsExportFullCircleTests : IDisposable
{
    private const double Tolerance = 0.01;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-fullcircle-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    [SkippableFact]
    public async Task FullCircle_ExportedGdsContainsRealLeafGeometryAndPinLabels()
    {
        var python = await FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — full-circle proof needs the real engine.");

        // 1. Import: TOP with two abutting 10×4 µm waveguide cells (wgA → wgB).
        var gdsPath = WriteFixtureGds();
        var service = _host.CreateService(() => Array.Empty<ComponentTemplate>());
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldBeEmpty();
        outcome.RegisteredComponents.Count.ShouldBe(2);

        // 2. Place: executor maps the plan onto the canvas (positions, connection, group).
        var canvas = new DesignCanvasViewModel();
        var plan = GdsPlacementPlan.FromOutcome(outcome);
        var report = await new GdsPlacementExecutor(canvas, null, () => _host.Templates.ToList())
            .ExecuteAsync(plan);
        report.PlacedCount.ShouldBe(2);
        report.ConnectedCount.ShouldBe(1);

        // 3. Export with raw-code inlining: the load_gds cells replace the box stubs.
        // The registered templates' RawCode carries the absolute path of the
        // materialized .gds in the design scope's cache — no fallback may trigger.
        var warnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            canvas, library: _host.Templates.ToList(), exportWarnings: warnings);
        warnings.ShouldBeEmpty("the materialized .gds cache copy backs the raw code");
        script.ShouldContain("component_wgA().put('org'");
        script.ShouldContain("component_wgB().put('org'");

        // 4. Run the script with real nazca → GDS next to the script.
        var exportDir = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "chip.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");

        var exportedGdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(exportedGdsPath).ShouldBeTrue($"script did not write {exportedGdsPath}:\n{run.StdOut}");

        // 5. Read the produced GDS back with our own reader and flatten the design cell.
        GdsLibrary library;
        await using (var stream = File.OpenRead(exportedGdsPath))
            library = await new GdsReader().ReadAsync(stream);
        library.Cells.ContainsKey("ConnectAPIC_Design").ShouldBeTrue(
            $"top cell missing; cells: {string.Join(", ", library.Cells.Keys)}");
        var flat = new GdsCellFlattener(library).Flatten("ConnectAPIC_Design");

        // 5a. Real leaf geometry: the 0.5 µm core stripes on (1,0) — a box stub would
        // draw one 10×4 µm rectangle instead. App placement (0,0)/(10,0) with the
        // exporter's Y negation puts both stripes at y ∈ [-2.25, -1.75].
        var stripes = flat.Polygons
            .Where(p => p.Layer == 1)
            .Select(p => (MinX: p.Points.Min(q => q.X), MaxX: p.Points.Max(q => q.X),
                          MinY: p.Points.Min(q => q.Y), MaxY: p.Points.Max(q => q.Y)))
            .ToList();
        stripes.Count.ShouldBeGreaterThanOrEqualTo(2,
            "two waveguide instances must contribute their core stripes (layer 1)");
        stripes.ShouldContain(s =>
            s.MaxY - s.MinY < 1.0 && InRange(s.MinX, 0) && InRange(s.MaxX, 10)
            && InRange(s.MinY, -2.25) && InRange(s.MaxY, -1.75),
            "wgA's core stripe (x 0..10, y -2.25..-1.75) — the REAL leaf geometry, not a box");
        stripes.ShouldContain(s =>
            s.MaxY - s.MinY < 1.0 && InRange(s.MinX, 10) && InRange(s.MaxX, 20)
            && InRange(s.MinY, -2.25) && InRange(s.MaxY, -1.75),
            "wgB's core stripe one cell-width to the right — positions consistent with the import");
        stripes.ShouldNotContain(s => s.MaxY - s.MinY > 3.9 && s.MaxX - s.MinX > 9.9,
            "a 10×4 full-box polygon on the waveguide layer would be the stub fallback");

        // 5b. Pin labels as TEXT records on (1, 10), anchored at the pins' world
        // positions (wgA: in (0,-2), out (10,-2); wgB: in (10,-2), out (20,-2)).
        var labels = flat.Texts.Where(t => t.Layer == 1 && t.TextType == 10).ToList();
        labels.ShouldContain(t => t.Text == "in" && InRange(t.Position.X, 0) && InRange(t.Position.Y, -2));
        labels.ShouldContain(t => t.Text == "out" && InRange(t.Position.X, 10) && InRange(t.Position.Y, -2));
        labels.ShouldContain(t => t.Text == "in" && InRange(t.Position.X, 10) && InRange(t.Position.Y, -2));
        labels.ShouldContain(t => t.Text == "out" && InRange(t.Position.X, 20) && InRange(t.Position.Y, -2));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static bool InRange(double value, double expected) =>
        Math.Abs(value - expected) < Tolerance;

    /// <summary>TOP with two abutting 10×4 µm waveguide cells (wgA → wgB), gdsfactory-style.</summary>
    private string WriteFixtureGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "circuit.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Locates a Python with nazca importable: first a Lunima managed env
    /// (%LOCALAPPDATA%/Lunima/envs/*), then python/python3 on PATH. The full-circle
    /// proof needs nothing beyond nazca — the produced GDS is read back with our
    /// own <see cref="GdsReader"/>, not with klayout/gdsfactory.
    /// </summary>
    private static async Task<string?> FindNazcaPythonAsync()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
        {
            foreach (var root in Directory.GetDirectories(envs))
            {
                foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
                {
                    var py = Path.Combine(root, rel);
                    if (File.Exists(py) && await ProbeNazca(py))
                        return py;
                }
            }
        }

        foreach (var candidate in new[] { "python", "python3" })
        {
            if (await ProbeNazca(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<bool> ProbeNazca(string python)
    {
        try
        {
            var probe = await SiepicRealGeometryExportTests.RunPythonAsync(
                python, Path.GetTempPath(), "-c", "import nazca");
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;   // not on PATH at all
        }
    }
}

/// <summary>GDS fixture cell builders (same shape as GdsImportServiceTests' waveguide cell).</summary>
file static class GdsFullCircleTestCells
{
    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on the waveguide
    /// layer (1,0), an extent rectangle on (111,0), and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}

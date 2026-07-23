using System.Diagnostics;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Repro for the PR #780 field finding: SiEPIC/nazca PDK components (ebeam_*) exported
/// as stub boxes, not real foundry geometry. The editor preview renders the REAL cell
/// via klayout (fixed-cell GDS / PCell from siepic_ebeam_pdk), while the nazca export
/// emitted a single <c>nd.Polygon</c> rectangle per component. The fix adds a klayout
/// post-pass to the generated script (<see cref="SiepicCellUpgradeWriter"/>) that swaps
/// the stub cell content for the real cell — same name, so instances stay put.
/// These tests run the generated script with the real managed env (siepic_ebeam_pdk
/// present) and count polygons in the written GDS: the stub box is exactly 1 polygon,
/// the real ebeam_dc_te1550 cell is more. The fallback test pins the contract that a
/// non-resolvable cell keeps the stub and only warns on stderr.
/// </summary>
[Trait("Category", "Slow")]
public class SiepicRealGeometryExportTests
{
    /// <summary>Lower bound separating a real foundry cell from the 1-polygon stub box.</summary>
    private const int RealCellMinPolygons = 2;

    [SkippableFact]
    public async Task NazcaExport_EbeamDirectionalCoupler_RendersRealGeometry_NotStubBox()
    {
        var python = FindSiepicPython();
        Skip.If(python == null, "No Lunima managed env with siepic_ebeam_pdk (expected on CI).");

        var script = new SimpleNazcaExporter().Export(EbeamCanvas());

        var result = await ExportAndCountPolygonsAsync(python, script, "siepic_repro_full");

        result.Polygons.ShouldBeGreaterThan(RealCellMinPolygons,
            "stub export draws one nd.Polygon box; the real siepic_ebeam_pdk ebeam_dc_te1550 cell has many polygons"
            + $"\nexport stderr: {result.StdErr}");
    }

    [SkippableFact]
    public async Task MixedBackendNazcaPartial_EbeamComponent_RendersRealGeometry_NotStubBox()
    {
        var python = FindSiepicPython();
        Skip.If(python == null, "No Lunima managed env with siepic_ebeam_pdk (expected on CI).");

        // The mixed-backend export (#776) renders its nazca-native group through the
        // same stub machinery, so the merged GDS shows the same box for ebeam cells.
        var script = new SimpleNazcaExporter().ExportPartial(
            EbeamCanvas(), _ => true, MixedBackendGdsOrchestrator.NazcaPartialTopCellName);

        var result = await ExportAndCountPolygonsAsync(python, script, "siepic_repro_partial");

        result.Polygons.ShouldBeGreaterThan(RealCellMinPolygons,
            "stub export draws one nd.Polygon box; the real siepic_ebeam_pdk ebeam_dc_te1550 cell has many polygons");
    }

    [SkippableFact]
    public async Task NazcaExport_UnresolvableSiepicCell_FallsBackToStub_WithWarning()
    {
        var python = FindSiepicPython();
        Skip.If(python == null, "No Lunima managed env with siepic_ebeam_pdk (expected on CI).");

        // Field decision: a cell that resolves neither as fixed-cell nor PCell must
        // never break the export — the stub box plus a stderr warning is the
        // documented fallback.
        var script = new SimpleNazcaExporter().Export(EbeamCanvas(funcName: "ebeam_does_not_exist_999"));

        var result = await ExportAndCountPolygonsAsync(python, script, "siepic_repro_fallback");

        result.Polygons.ShouldBe(1, "an unresolvable cell must keep the 1-polygon stub box");
        result.StdErr.ShouldContain("[Lunima] WARN: real SiEPIC cell 'ebeam_does_not_exist_999' unavailable");
    }

    /// <summary>One Directional Coupler TE 1550 from the SiEPIC EBeam PDK (PDK JSON dims/offsets).</summary>
    private static DesignCanvasViewModel EbeamCanvas(
        string nazcaModule = "siepic_ebeam_pdk", string funcName = "ebeam_dc_te1550")
    {
        var canvas = new DesignCanvasViewModel();
        var dc = TestComponentFactory.CreateBasicComponent();
        dc.Identifier = "DC1";
        dc.NazcaFunctionName = funcName;
        dc.NazcaModuleName = nazcaModule;
        dc.NazcaFunctionParameters = "gap=200E-9";
        dc.WidthMicrometers = 22.02;
        dc.HeightMicrometers = 6.2;
        dc.NazcaOriginOffsetX = 11.01;
        dc.NazcaOriginOffsetY = 3.1;
        canvas.AddComponent(dc, "Directional Coupler TE 1550");
        return canvas;
    }

    /// <summary>
    /// Writes the script to a temp dir, runs it with the managed env Python (the script
    /// calls <c>nd.export_gds()</c> next to itself), then counts all shapes of the top
    /// cell recursively via klayout (present in the managed env).
    /// </summary>
    private static async Task<(int Polygons, string StdErr)> ExportAndCountPolygonsAsync(
        string python, string script, string stem)
    {
        var dir = Path.Combine(Path.GetTempPath(), "siepic_repro_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, stem + ".py");
            await File.WriteAllTextAsync(scriptPath, script);

            var run = await RunPythonAsync(python, dir, scriptPath);
            run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");

            var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
            File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

            const string counter =
                "import sys, klayout.db as db\n" +
                "ly = db.Layout()\n" +
                "ly.read(sys.argv[1])\n" +
                // Sum shapes over every cell and layer — robust against multi-top GDS
                // (nazca writes library cells alongside the design cell).
                "n = 0\n" +
                "for c in ly.each_cell():\n" +
                "    for li in ly.layer_indexes():\n" +
                "        n += c.shapes(li).size()\n" +
                "print(n)";
            var count = await RunPythonAsync(python, dir, "-c", counter, gdsPath);
            count.ExitCode.ShouldBe(0, $"klayout polygon count failed:\n{count.StdErr}");
            return (int.Parse(count.StdOut.Trim()), run.StdErr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup best effort */ }
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPythonAsync(
        string python, string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo { WorkingDirectory = workingDir };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.FileName = python;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using var process = Process.Start(psi)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Python did not finish within 4 minutes: {string.Join(' ', args)}");
        }
        return (process.ExitCode, await stdOut, await stdErr);
    }

    /// <summary>Walks up from the test output dir to the repo root (temporary debug dump).</summary>
    private static string FindRepoRootForDump()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CAP.Avalonia", "App.axaml.cs")))
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetTempPath();
    }

    /// <summary>Locates a Lunima managed env (%LOCALAPPDATA%/Lunima/envs/*) with siepic_ebeam_pdk installed.</summary>
    private static string? FindSiepicPython()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (!Directory.Exists(envs))
            return null;

        foreach (var root in Directory.GetDirectories(envs))
        {
            if (!HasSiepicPdk(root)) continue;
            foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
            {
                var py = Path.Combine(root, rel);
                if (File.Exists(py)) return py;
            }
        }
        return null;
    }

    private static bool HasSiepicPdk(string envRoot)
    {
        if (Directory.Exists(Path.Combine(envRoot, "Lib", "site-packages", "siepic_ebeam_pdk")))
            return true;   // Windows venv layout
        var lib = Path.Combine(envRoot, "lib");
        return Directory.Exists(lib)
            && Directory.GetDirectories(lib).Any(d => Directory.Exists(Path.Combine(d, "site-packages", "siepic_ebeam_pdk")));
    }
}

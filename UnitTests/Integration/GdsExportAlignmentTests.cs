using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;

namespace UnitTests.Integration;

/// <summary>
/// Acceptance matrix for issue #565: {PDK component, raw-code override} × {0°, 90°,
/// 180°, 270°}. Each cell exports the design with the verification epilog, EXECUTES
/// the script with real nazca and compares the engine-reported world pin positions
/// (.pins.json) against <see cref="NazcaCoordinateMapper"/> — so the alignment
/// contract is checked by the same nazca engine that writes the GDS.
///
/// Requires a nazca-capable Python (resolved via <see cref="PythonDiscoveryService"/>);
/// skips cleanly when none is available so CI without nazca still passes.
/// </summary>
public class GdsExportAlignmentTests
{
    private const double ToleranceMicrometers = 0.01;
    private const double ToleranceDegrees = 0.01;

    private readonly System.Collections.ObjectModel.ObservableCollection<ComponentTemplate> _library;

    public GdsExportAlignmentTests()
    {
        _library = new System.Collections.ObjectModel.ObservableCollection<ComponentTemplate>(
            TestPdkLoader.LoadAllTemplates());
    }

    /// <summary>Nazca bookkeeping pins every cell ships; they are not user pins.</summary>
    // Must match INTERNAL in scripts/render_component_preview.py — keep in sync manually.
    private static readonly HashSet<string> NazcaInternalPins = new(StringComparer.Ordinal)
    {
        "org", "cc", "lb", "lc", "lt", "tl", "tc", "tr", "rt", "rc", "rb", "br", "bc", "bl"
    };

    [Fact]
    public async Task PdkComponent_AllRotations_PinsAlignInGds()
    {
        var (python, _) = await GdsAlignmentTestSetup.ResolveEnvironmentAsync();
        if (python == null) return;   // env skip (CI without nazca)

        // "2x2 MMI Coupler" — calibrated against the real demofab cell (org at the
        // left-centre, offset (0, 30)), so its rendered pins must hit the app pins.
        var template = _library
            .First(t => t.NazcaFunctionName == "demo.mmi2x2_dp");
        var canvas = new DesignCanvasViewModel();
        var dut = ComponentTemplates.CreateFromTemplate(template, 100, 400);
        dut.Identifier = "align_dut";
        var dutVm = canvas.AddComponent(dut, template.Name);
        var partner = ComponentTemplates.CreateFromTemplate(template, 900, 400);
        partner.Identifier = "align_partner";
        canvas.AddComponent(partner, template.Name);

        canvas.ConnectPins(
            dut.PhysicalPins.First(p => p.Name == "out1"),
            partner.PhysicalPins.First(p => p.Name == "in1"));

        await RunRotationMatrixAsync(canvas, dutVm, dut, python);
    }

    [Fact]
    public async Task DemoShallowBend_AllRotations_PinsAlignInGds()
    {
        var (python, _) = await GdsAlignmentTestSetup.ResolveEnvironmentAsync();
        if (python == null) return;   // env skip (CI without nazca)

        // Issue #635: demo.shallow.bend is the only bundled demofab cell with a
        // Y-ASYMMETRIC bbox (Y ∈ [-9.4, 200], org = a0 at the cell origin). The
        // stale bottom-edge calibration (NazcaOriginOffsetY = -YMin = 9.4)
        // placed the rendered bend 190.6 µm above its canvas rectangle; the
        // engine-reported pins expose that here — a Y-symmetric DUT like
        // mmi2x2_dp can never catch this convention mismatch.
        var template = _library.First(t => t.NazcaFunctionName == "demo.shallow.bend");
        var canvas = new DesignCanvasViewModel();
        var dut = ComponentTemplates.CreateFromTemplate(template, 100, 400);
        dut.Identifier = "bend_dut";
        var dutVm = canvas.AddComponent(dut, template.Name);

        await RunRotationMatrixAsync(canvas, dutVm, dut, python);
    }

    [Fact]
    public async Task ParametricStraight_Rotated_PinsAlignInGds()
    {
        var (python, _) = await GdsAlignmentTestSetup.ResolveEnvironmentAsync();
        if (python == null) return;   // env skip

        // A parametric demo_pdk.straight with NazcaOriginOffset=(0,0) and pins on the
        // centre line (OffsetY = H/2). The mapper anchors parametric straights on the
        // first pin (oy = H/2), but the stub used NazcaOriginOffsetY (=0), so before
        // the fix the rendered waveguide/pins sat H/2 below where the mapper claims —
        // a real GDS misalignment that the engine-reported pins expose here.
        var canvas = new DesignCanvasViewModel();
        var dut = CreateParametricStraight("align_strt_dut", 100, lengthMicrometers: 200, x: 100, y: 400);
        var dutVm = canvas.AddComponent(dut);
        var partner = CreateParametricStraight("align_strt_partner", 100, lengthMicrometers: 200, x: 900, y: 400);
        canvas.AddComponent(partner);

        canvas.ConnectPins(
            dut.PhysicalPins.First(p => p.Name == "b0"),
            partner.PhysicalPins.First(p => p.Name == "a0"));

        // 0° and 90° are sufficient to catch the anchor mismatch on both axes.
        await RunRotationMatrixAsync(canvas, dutVm, dut, python, maxSteps: 2);
    }

    /// <summary>
    /// Builds a parametric straight waveguide (demo_pdk.straight, length=...) with the
    /// origin offset left at (0,0) and both pins on the cell centre line (OffsetY = H/2),
    /// mirroring the SimpleNazcaExporterTests straight fixture.
    /// </summary>
    private static Component CreateParametricStraight(
        string identifier, double heightMicrometers, double lengthMicrometers, double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var comp = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "demo_pdk.straight",
            nazcaFunctionParams: $"length={lengthMicrometers.ToString(CultureInfo.InvariantCulture)}",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0);
        comp.WidthMicrometers = lengthMicrometers;
        comp.HeightMicrometers = heightMicrometers;
        comp.PhysicalX = x;
        comp.PhysicalY = y;
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = heightMicrometers / 2,
            AngleDegrees = 180, ParentComponent = comp
        });
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "b0", OffsetXMicrometers = lengthMicrometers, OffsetYMicrometers = heightMicrometers / 2,
            AngleDegrees = 0, ParentComponent = comp
        });
        return comp;
    }

    // ── Matrix driver ────────────────────────────────────────────────────────

    /// <summary>
    /// Rotates the DUT through 0/90/180/270° via the REAL rotate command, exporting
    /// and python-verifying after each step. A loop instead of a Theory keeps the
    /// python startup cost at one run per rotation instead of per test case.
    /// </summary>
    private static async Task RunRotationMatrixAsync(
        DesignCanvasViewModel canvas, ComponentViewModel dutVm, Component dut,
        string python, int maxSteps = 4)
    {
        for (int rotationSteps = 0; rotationSteps < maxSteps; rotationSteps++)
        {
            if (rotationSteps > 0)
            {
                var cmd = new RotateComponentCommand(canvas, dutVm);
                cmd.Execute();
                cmd.WasApplied.ShouldBeTrue($"rotation step {rotationSteps} must not be blocked");
            }
            await canvas.RecalculateRoutesAsync();

            var script = new SimpleNazcaExporter().Export(
                canvas, emitVerification: true);
            var (pins, scriptPath, tempDir) = await RunScriptAsync(script, python, rotationSteps);

            AssertDutPinsAlign(dut, pins["comp_0"], rotationSteps, scriptPath);
            // Connection-less designs (single-DUT placement checks) skip the
            // waveguide assertions — there is no route to verify.
            if (canvas.Connections.Count > 0)
                AssertConnectionHitsPins(canvas, script, rotationSteps);

            // Reached only when this rotation passed — failures keep the script,
            // .gds and .pins.json on disk for analysis.
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Writes the script to a temp dir, executes it and parses .pins.json.</summary>
    private static async Task<(Dictionary<string, Dictionary<string, double[]>> Pins,
        string ScriptPath, string TempDir)> RunScriptAsync(
        string script, string python, int rotationSteps)
    {
        var tempDir = Directory.CreateTempSubdirectory("lunima_align_").FullName;
        var scriptPath = Path.Combine(tempDir, $"alignment_rot{rotationSteps}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        var gdsService = new GdsExportService();
        gdsService.SetCustomPythonPath(python);
        var result = await gdsService.ExportToGdsAsync(scriptPath, generateGds: true);

        result.Success.ShouldBeTrue(
            $"rot={rotationSteps * 90}°: generated script must run without errors. " +
            $"Error: {result.ErrorMessage}\n--- script ---\n{script}");
        File.Exists(result.GdsPath).ShouldBeTrue(
            $"rot={rotationSteps * 90}°: a .gds file must be produced");

        var pinsJsonPath = Path.ChangeExtension(scriptPath, ".pins.json");
        File.Exists(pinsJsonPath).ShouldBeTrue(
            $"rot={rotationSteps * 90}°: the verification epilog must write the pins json");
        var pins = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(
            await File.ReadAllTextAsync(pinsJsonPath))!;
        return (pins, scriptPath, tempDir);
    }

    // ── Asserts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every app pin of the DUT must coincide with a rendered instance pin — in
    /// position AND direction. Override cells expose the app pin names directly; PDK
    /// cells use nazca's own naming (a0/b0/…), so there the geometrically nearest
    /// rendered pin must coincide — then the angle check guards against a wrong pin
    /// merely passing by (e.g. a mirrored cell). Note mmi2x2_dp is 180°-symmetric
    /// (a0↔b1 incl. angles), so a put-rotation SIGN error renders identical GDS for
    /// it; that error class is caught by the asymmetric override cell's matrix.
    /// </summary>
    private static void AssertDutPinsAlign(
        Component dut, Dictionary<string, double[]> instancePins, int rotationSteps,
        string scriptPath)
    {
        var rendered = instancePins
            .Where(kv => !NazcaInternalPins.Contains(kv.Key)).ToList();
        rendered.ShouldNotBeEmpty(
            $"rot={rotationSteps * 90}°: the placed instance must expose rendered pins");

        foreach (var pin in dut.PhysicalPins)
        {
            var (wantX, wantY) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);
            var (matchName, actual) = instancePins.TryGetValue(pin.Name, out var byName)
                ? (pin.Name, byName)
                : rendered.OrderBy(kv => Distance(kv.Value, wantX, wantY))
                    .Select(kv => (kv.Key, kv.Value)).First();

            Distance(actual, wantX, wantY).ShouldBeLessThan(ToleranceMicrometers,
                $"pin '{pin.Name}' (nazca pin '{matchName}') at rot={rotationSteps * 90}°: " +
                $"actual=({actual[0]:F3},{actual[1]:F3}) expected=({wantX:F3},{wantY:F3}) " +
                $"script={scriptPath}");

            var wantAngle = NazcaCoordinateMapper.GetPinNazcaAngle(pin);
            AngleDifference(actual[2], wantAngle).ShouldBeLessThan(ToleranceDegrees,
                $"pin '{pin.Name}' (nazca pin '{matchName}') at rot={rotationSteps * 90}°: " +
                $"angle actual={actual[2]:F1}° expected={wantAngle:F1}° script={scriptPath}");
        }
    }

    /// <summary>Absolute angular difference folded into [0, 180].</summary>
    private static double AngleDifference(double a, double b) =>
        Math.Abs((((a - b) % 360) + 540) % 360 - 180);

    /// <summary>
    /// The emitted waveguide geometry must hit both connection pins: the routed path's
    /// endpoints convert to the pins' nazca positions, and the script's first absolute
    /// segment put sits on the start pin.
    /// </summary>
    private static void AssertConnectionHitsPins(
        DesignCanvasViewModel canvas, string script, int rotationSteps)
    {
        var conn = canvas.Connections.Single().Connection;
        var segments = conn.GetPathSegments();
        segments.Count.ShouldBeGreaterThan(0,
            $"rot={rotationSteps * 90}°: the connection must carry a routed path");

        var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(conn.StartPin!);
        var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(conn.EndPin!);
        var (fx, fy) = NazcaCoordinateMapper.ToNazca(
            segments[0].StartPoint.X, segments[0].StartPoint.Y);
        var (lx, ly) = NazcaCoordinateMapper.ToNazca(
            segments[^1].EndPoint.X, segments[^1].EndPoint.Y);

        Distance(new[] { fx, fy }, sx, sy).ShouldBeLessThan(ToleranceMicrometers,
            $"rot={rotationSteps * 90}°: route start ({fx:F3},{fy:F3}) must hit start pin ({sx:F3},{sy:F3})");
        Distance(new[] { lx, ly }, ex, ey).ShouldBeLessThan(ToleranceMicrometers,
            $"rot={rotationSteps * 90}°: route end ({lx:F3},{ly:F3}) must hit end pin ({ex:F3},{ey:F3})");

        var (px, py) = FirstWaveguidePut(script);
        Distance(new[] { px, py }, sx, sy).ShouldBeLessThan(ToleranceMicrometers,
            $"rot={rotationSteps * 90}°: first emitted segment put ({px:F3},{py:F3}) " +
            $"must sit on start pin ({sx:F3},{sy:F3})");
    }

    /// <summary>Parses the first absolute waveguide put after the connections marker.</summary>
    private static (double X, double Y) FirstWaveguidePut(string script)
    {
        var idx = script.IndexOf("# Waveguide Connections", StringComparison.Ordinal);
        idx.ShouldBeGreaterThan(-1, "script must contain a connections section");
        var match = Regex.Match(
            script[idx..], @"nd\.(strt|bend)\([^\n]*\.put\((-?\d+\.\d+), (-?\d+\.\d+)");
        match.Success.ShouldBeTrue("script must emit an absolute first waveguide segment");
        return (double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private static double Distance(double[] actual, double x, double y) =>
        Math.Sqrt(Math.Pow(actual[0] - x, 2) + Math.Pow(actual[1] - y, 2));
}

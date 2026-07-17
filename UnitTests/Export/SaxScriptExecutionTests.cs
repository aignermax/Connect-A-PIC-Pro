using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Component = CAP_Core.Components.Core.Component;

namespace UnitTests.Export;

/// <summary>
/// End-to-end tests for <see cref="SaxExporter"/>: the generated script is
/// executed with real sax, numpy, and matplotlib and the resulting circuit
/// state is introspected and asserted on. Catches the class of bug that
/// string-contains assertions miss — e.g. a <c>connect()</c> pointing at a
/// component that was never added, a malformed S-matrix array, or an analytic
/// waveguide model whose closed-form transmission doesn't match physics.
///
/// <para>
/// Skipped when python + sax + numpy + matplotlib are not reachable. CI
/// installs them via <c>.github/workflows/xUnitTests.yaml</c>. The probe
/// tries, in order: <c>LUNIMA_PICWAVE_PYTHON</c>, <c>~/picwave-venv/bin/python</c>,
/// <c>python3</c>, <c>python</c>.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class SaxScriptExecutionTests
{
    private readonly ITestOutputHelper _output;
    private readonly SaxExporter _exporter = new();

    public SaxScriptExecutionTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void ExportedScript_WithMeasuredSMatrix_RunsAndReturnsExpectedTransmission()
    {
        SkipIfPythonMissing(out var python);

        // 2-port component with S12 = S21 = 0.9. One component → two dangling
        // pins → two external ports. Transmission between them should be
        // |0.9|² = 0.81 regardless of wavelength (no dispersion in the model).
        var comp = TestComponentFactory.CreatePhaseShifterWithPhysicalPins(
            forward:  new Complex(0.9, 0),
            backward: new Complex(0.9, 0));
        comp.Identifier = "ps_meas";

        var script = _exporter.Export([comp], []);
        var result = Run(python, script);
        result.ExitCode.ShouldBe(0, $"script must run cleanly.\nstderr:\n{result.Stderr}\n");

        // Single component with its two pins as external ports.
        result.Instances.ShouldBe(new[] { "ps_meas" }, ignoreOrder: true);
        result.PortCount.ShouldBe(2);
        result.Transmission.Count.ShouldBeGreaterThan(0);
        foreach (var t in result.Transmission)
            t.ShouldBe(0.81, 0.01, "|S21|² for a 0.9-transmission component");
    }

    [SkippableFact]
    public void ExportedScript_GroupWithInternalAndExternalConnections_BuildsAllEdges()
    {
        // The user's actual scenario: three components chained inside a group
        // (internal FrozenWaveguidePath edges), one external component, and one
        // cross-group connection. All four wiring edges must end up in sax's
        // netlist, leaving exactly two dangling pins as circuit ports.
        SkipIfPythonMissing(out var python);

        var a = MakeWaveguide("inner_a");
        var b = MakeWaveguide("inner_b");
        var c = MakeWaveguide("inner_c");
        var outside = MakeWaveguide("outside");

        var group = new ComponentGroup("chain");
        group.Identifier = "chain_group";
        group.AddChild(a);
        group.AddChild(b);
        group.AddChild(c);
        group.InternalPaths.Add(new FrozenWaveguidePath
        {
            StartPin = a.PhysicalPins[1],
            EndPin = b.PhysicalPins[0],
            Path = new RoutedPath(),
        });
        group.InternalPaths.Add(new FrozenWaveguidePath
        {
            StartPin = b.PhysicalPins[1],
            EndPin = c.PhysicalPins[0],
            Path = new RoutedPath(),
        });
        group.ExternalPins.Add(new GroupPin
        {
            Name = "out",
            InternalPin = c.PhysicalPins[1],
        });
        group.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out",
            ParentComponent = group,
            LogicalPin = c.PhysicalPins[1].LogicalPin,
        });

        var conn = new WaveguideConnection
        {
            StartPin = group.PhysicalPins[0],
            EndPin = outside.PhysicalPins[0],
        };

        var script = _exporter.Export([group, outside], [conn]);
        var result = Run(python, script);
        result.ExitCode.ShouldBe(0, $"script must run cleanly.\nstderr:\n{result.Stderr}\n");

        // Group is flattened: the netlist references leaf components only.
        result.Instances.ShouldBe(
            new[] { "inner_a", "inner_b", "inner_c", "outside" }, ignoreOrder: true);

        // All three internal + one external edge are present.
        result.Connections.ShouldContain("inner_a,out1->inner_b,in");
        result.Connections.ShouldContain("inner_b,out1->inner_c,in");
        result.Connections.ShouldContain("inner_c,out1->outside,in");

        // Dangling pins: inner_a.in and outside.out1 — exactly two ports.
        result.PortCount.ShouldBe(2);

        // Four 50 µm waveguides at 2 dB/cm → 0.04 dB total → |S|² ≈ 0.9908.
        // The analytic model is wavelength-agnostic for amplitude (phase only).
        result.Transmission[0].ShouldBe(0.9908, 0.001);
    }

    [SkippableFact]
    public void ExportedScript_WavelengthSweep_PopulatesExpectedNumberOfPoints()
    {
        SkipIfPythonMissing(out var python);

        var a = MakeWaveguide("wg");
        var script = _exporter.Export(
            [a], [],
            wavelengthMinNm: 1500,
            wavelengthMaxNm: 1600,
            numPoints: 7);

        var result = Run(python, script);
        result.ExitCode.ShouldBe(0, $"stderr:\n{result.Stderr}");
        result.Transmission.Count.ShouldBe(7);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Component MakeWaveguide(string identifier)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var c = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "ebeam_wg_strip_straight",
            "",
            parts,
            0,
            identifier,
            DiscreteRotation.R0,
            new List<PhysicalPin>());
        c.PhysicalPins.Add(new PhysicalPin { Name = "in",   ParentComponent = c });
        c.PhysicalPins.Add(new PhysicalPin { Name = "out1", ParentComponent = c });
        c.WidthMicrometers = 50;
        c.HeightMicrometers = 10;
        return c;
    }

    private void SkipIfPythonMissing(out string pythonExe)
    {
        // Allow explicit override (useful for `dotnet test` on dev machines
        // where sax lives in a non-default venv).
        var env = Environment.GetEnvironmentVariable("LUNIMA_PICWAVE_PYTHON");
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);
        if (!string.IsNullOrEmpty(home))
            candidates.Add(Path.Combine(home, "picwave-venv", "bin", "python"));
        candidates.Add("python3");
        candidates.Add("python");

        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            if (TryProbe(candidate, out var reason))
            {
                pythonExe = candidate;
                _output.WriteLine($"using python: {candidate}");
                return;
            }
            reasons.Add($"{candidate}: {reason}");
            _output.WriteLine($"probe '{candidate}' failed: {reason}");
        }

        pythonExe = "";
        Skip.If(true,
            "Python with sax + numpy + matplotlib not found. Set LUNIMA_PICWAVE_PYTHON " +
            "or `pip install sax numpy matplotlib` into python3. Probed:\n  " +
            string.Join("\n  ", reasons));
    }

    private static bool TryProbe(string pythonExe, out string reason)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sax, numpy, matplotlib; matplotlib.use('Agg')");

            using var p = Process.Start(psi);
            if (p == null) { reason = "Process.Start returned null"; return false; }
            p.WaitForExit(30_000);
            if (p.ExitCode != 0)
            {
                reason = $"exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}";
                return false;
            }
            reason = "";
            return true;
        }
        catch (Win32Exception ex)            { reason = $"Win32Exception: {ex.Message}"; return false; }
        catch (FileNotFoundException ex)     { reason = $"FileNotFoundException: {ex.Message}"; return false; }
        catch (InvalidOperationException ex) { reason = $"InvalidOperationException: {ex.Message}"; return false; }
    }

    private readonly record struct RunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        IReadOnlyList<string> Instances,
        IReadOnlyList<string> Connections,
        int PortCount,
        IReadOnlyList<double> Transmission);

    /// <summary>
    /// Executes the generated script headlessly, then captures the resulting
    /// namespace and prints a JSON payload the test can parse. The script is
    /// run via <c>runpy.run_path</c> so module-level constants (netlist,
    /// transmission, INPUT_PORT, OUTPUT_PORT) survive into the capture step.
    /// </summary>
    private RunResult Run(string pythonExe, string exportedScript)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"lunima_picwave_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, exportedScript,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Wrapper: sets Agg backend, runs the script, then emits a JSON line
        // prefixed with LUNIMA_CAPTURE: that the test parses.
        var wrapper = $$"""
            import json, sys, runpy
            import matplotlib
            matplotlib.use("Agg")
            ns = runpy.run_path({{JsonSerializer.Serialize(scriptPath)}})
            nl = ns["netlist"]
            def _short(conns):
                return ["->".join([k, v]) for k, v in conns.items()]
            payload = {
                "instances":   list(nl["instances"]),
                "connections": _short(nl["connections"]),
                "port_count":  len(nl["ports"]),
                "transmission": [float(x) for x in ns.get("transmission", [])],
            }
            print("LUNIMA_CAPTURE:" + json.dumps(payload))
            """;
        var wrapperPath = Path.Combine(Path.GetTempPath(),
            $"lunima_picwave_run_{Guid.NewGuid():N}.py");
        File.WriteAllText(wrapperPath, wrapper,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            var psi = new ProcessStartInfo(pythonExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(wrapperPath);

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{pythonExe}'");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(120_000);
            Task.WaitAll(stdoutTask, stderrTask);

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            _output.WriteLine("stdout:\n" + stdout);
            if (!string.IsNullOrEmpty(stderr))
                _output.WriteLine("stderr:\n" + stderr);

            return ParseCapture(p.ExitCode, stdout, stderr);
        }
        finally
        {
            if (File.Exists(scriptPath))  File.Delete(scriptPath);
            if (File.Exists(wrapperPath)) File.Delete(wrapperPath);
        }
    }

    private static RunResult ParseCapture(int exitCode, string stdout, string stderr)
    {
        var line = stdout
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("LUNIMA_CAPTURE:", StringComparison.Ordinal));
        if (line == null)
            return new RunResult(exitCode, stdout, stderr,
                Array.Empty<string>(), Array.Empty<string>(), 0, Array.Empty<double>());

        var json = line["LUNIMA_CAPTURE:".Length..].Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var instances = root.GetProperty("instances")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var connections = root.GetProperty("connections")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var portCount = root.GetProperty("port_count").GetInt32();
        var transmission = root.GetProperty("transmission")
            .EnumerateArray().Select(e => e.GetDouble()).ToList();

        return new RunResult(exitCode, stdout, stderr,
            instances, connections, portCount, transmission);
    }
}

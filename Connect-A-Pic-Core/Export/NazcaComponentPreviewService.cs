using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CAP_Core.Export;

/// <summary>Polygon data returned by the Nazca preview script.</summary>
public class NazcaPreviewPolygon
{
    /// <summary>GDS layer number.</summary>
    public int Layer { get; init; }
    /// <summary>Polygon vertices in Nazca coordinate space (µm).</summary>
    public IReadOnlyList<(double X, double Y)> Vertices { get; init; } = Array.Empty<(double, double)>();
}

/// <summary>Pin stub data returned by the Nazca preview script.</summary>
public class NazcaPreviewPin
{
    /// <summary>Pin name (e.g. "a0").</summary>
    public string Name { get; init; } = "";
    /// <summary>Pin X in Nazca space (µm).</summary>
    public double X { get; init; }
    /// <summary>Pin Y in Nazca space (µm).</summary>
    public double Y { get; init; }
    /// <summary>Pin angle in degrees.</summary>
    public double Angle { get; init; }
    /// <summary>Stub far endpoint X (µm).</summary>
    public double StubX1 { get; init; }
    /// <summary>Stub far endpoint Y (µm).</summary>
    public double StubY1 { get; init; }
}

/// <summary>Result from <see cref="NazcaComponentPreviewService.RenderAsync"/>.</summary>
public class NazcaPreviewResult
{
    /// <summary>True when the script ran successfully.</summary>
    public bool Success { get; init; }
    /// <summary>Error description when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
    /// <summary>Nazca bounding-box extents (µm).</summary>
    public double XMin { get; init; }
    /// <inheritdoc cref="XMin"/>
    public double YMin { get; init; }
    /// <inheritdoc cref="XMin"/>
    public double XMax { get; init; }
    /// <inheritdoc cref="XMin"/>
    public double YMax { get; init; }
    /// <summary>GDS polygons (empty when gdstk/gdspy is not installed).</summary>
    public IReadOnlyList<NazcaPreviewPolygon> Polygons { get; init; } = Array.Empty<NazcaPreviewPolygon>();
    /// <summary>
    /// Non-fatal warning from the preview script — typically reports that
    /// gdstk/gdspy is missing so the polygon overlay couldn't be populated.
    /// Surfaces in the editor's status text alongside the success message.
    /// </summary>
    public string? PolygonWarning { get; init; }
    /// <summary>
    /// Real PDK source for the rendered component — the actual Nazca cell
    /// function body via <c>inspect.getsource</c>, or the SiEPIC PCell
    /// Python file content. <c>null</c> when the script could not retrieve it.
    /// </summary>
    public string? Source { get; init; }
    /// <summary>Pin stubs.</summary>
    public IReadOnlyList<NazcaPreviewPin> Pins { get; init; } = Array.Empty<NazcaPreviewPin>();

    /// <summary>Returns a failure result with the given error message.</summary>
    public static NazcaPreviewResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Invokes <c>scripts/render_component_preview.py</c> to render a Nazca component
/// cell and returns bounding-box, polygon and pin data for the PDK Offset Editor overlay.
/// Results are cached by (module, function, parameters) key.
/// </summary>
public class NazcaComponentPreviewService
{
    /// <summary>Default subprocess timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly ProcessLaunchFactory _launchFactory;
    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, NazcaPreviewResult> _cache = new();

    /// <summary>
    /// Initializes the service.
    /// </summary>
    /// <param name="launchFactory">Factory used to build process start info.</param>
    /// <param name="pythonExecutable">Path to Python 3 executable.</param>
    /// <param name="scriptPath">Absolute path to render_component_preview.py.</param>
    /// <param name="timeout">Optional subprocess timeout.</param>
    public NazcaComponentPreviewService(string pythonExecutable, string scriptPath, TimeSpan? timeout = null, ProcessLaunchFactory? launchFactory = null)
    {
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
        _pythonExecutable = pythonExecutable ?? throw new ArgumentNullException(nameof(pythonExecutable));
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Renders the component preview, using a cached result when available.
    /// Never throws — returns a failure result instead.
    /// </summary>
    public virtual async Task<NazcaPreviewResult> RenderAsync(
        string? moduleName, string nazcaFunction, string? nazcaParameters,
        CancellationToken ct = default)
    {
        var key = $"{moduleName ?? ""}|{nazcaFunction}|{nazcaParameters ?? ""}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await RunScriptAsync(moduleName, nazcaFunction, nazcaParameters, ct);
        if (result.Success)
            _cache[key] = result;
        return result;
    }

    /// <summary>
    /// Renders a preview by executing raw, user-supplied Nazca cell code (issue #556).
    /// The code is written to a temporary <c>.py</c> file and passed to the preview
    /// script in <c>--code-file</c> mode, where its <c>component()</c> callable (or a
    /// module-level <c>cell</c> variable) builds the geometry. Results are cached by a
    /// hash of the code. Never throws — returns a failure result instead. The subprocess
    /// timeout/kill guards against syntax errors hanging or infinite loops.
    /// </summary>
    /// <param name="code">Complete Nazca cell code to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<NazcaPreviewResult> RenderRawCodeAsync(string code, CancellationToken ct = default)
    {
        var key = "rawcode|" + ComputeCodeHash(code);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await RunRawCodeScriptAsync(code, ct);
        if (result.Success)
            _cache[key] = result;
        return result;
    }

    private static string ComputeCodeHash(string code)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(code ?? string.Empty));
        return Convert.ToHexString(bytes);
    }

    private async Task<NazcaPreviewResult> RunRawCodeScriptAsync(string code, CancellationToken ct)
    {
        if (!File.Exists(_scriptPath))
            return NazcaPreviewResult.Fail($"Preview script not found: {_scriptPath}");

        var tempFile = Path.Combine(Path.GetTempPath(), $"lunima_rawcode_{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(tempFile, code ?? string.Empty, ct);
            return await RunProcessAsync(
                new[] { _scriptPath, "--code-file", tempFile }, ct);
        }
        catch (Exception ex)
        {
            return NazcaPreviewResult.Fail($"Unexpected error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private async Task<NazcaPreviewResult> RunScriptAsync(
        string? moduleName, string nazcaFunction, string? nazcaParameters, CancellationToken ct)
    {
        if (!File.Exists(_scriptPath))
            return NazcaPreviewResult.Fail($"Preview script not found: {_scriptPath}");

        var module = string.IsNullOrWhiteSpace(moduleName) ? "demo" : moduleName;
        var args = new List<string> { _scriptPath, module, nazcaFunction };
        if (!string.IsNullOrWhiteSpace(nazcaParameters))
            args.Add(nazcaParameters);
        return await RunProcessAsync(args, ct);
    }

    /// <summary>
    /// Shared subprocess plumbing: starts Python on the script with the given
    /// arguments (first element must be the script path), enforces the timeout/kill,
    /// and parses stdout. Construction is delegated to <see cref="_launchFactory"/>
    /// so that WorkingDirectory and augmented PATH are applied consistently.
    /// </summary>
    private async Task<NazcaPreviewResult> RunProcessAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(_scriptPath);
            if (!_launchFactory.TryBuild(_pythonExecutable, arguments, workingDir, null, out var psi, out var launchError))
                return NazcaPreviewResult.Fail($"Could not start Python '{_pythonExecutable}': {launchError}");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi);
            if (process == null)
                return NazcaPreviewResult.Fail($"Could not start Python '{_pythonExecutable}'.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(_timeout, ct);
            var completed = await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), timeoutTask);

            if (completed == timeoutTask || ct.IsCancellationRequested)
            {
                TryKill(process);
                return ct.IsCancellationRequested
                    ? NazcaPreviewResult.Fail("Operation was cancelled.")
                    : NazcaPreviewResult.Fail($"Preview script timed out after {_timeout.TotalSeconds:F0}s.");
            }

            await process.WaitForExitAsync(ct);
            return ParseOutput(await stdoutTask);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return NazcaPreviewResult.Fail($"Could not start Python '{_pythonExecutable}': {ex.Message}");
        }
        catch (Exception ex)
        {
            return NazcaPreviewResult.Fail($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the JSON document the Python helper script writes on stdout.
    /// Exposed as <c>internal</c> so unit tests can exercise the JSON path
    /// without spawning a real subprocess (which the CI Linux box may lack).
    /// </summary>
    internal static NazcaPreviewResult ParseOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return NazcaPreviewResult.Fail("Preview script produced no output.");

        // Nazca writes log lines (e.g. "INFO   : pin2pin drc: True") on stdout
        // through sys.__stdout__, which bypasses Python's contextlib.redirect_stdout
        // in the helper script. Pick the LAST line that parses as JSON instead
        // of trying to parse the whole stdout — that's the result line we wrote.
        var jsonLine = ExtractTrailingJsonLine(stdout);
        if (jsonLine == null)
            return NazcaPreviewResult.Fail(
                $"failed to parse preview output, no JSON object found in stdout: {Truncate(stdout, 200)}");

        try
        {
            // 'using' returns the rented buffer to ArrayPool eagerly; without
            // it Check-All on a 40-component PDK keeps ~40 buffers out of the
            // pool until the next GC pass.
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var sp) && !sp.GetBoolean())
            {
                var msg = root.TryGetProperty("error", out var ep) ? ep.GetString() : null;
                return NazcaPreviewResult.Fail(msg ?? "Unknown error");
            }

            var bbox = root.GetProperty("bbox");
            return new NazcaPreviewResult
            {
                Success = true,
                XMin = bbox.GetProperty("xmin").GetDouble(),
                YMin = bbox.GetProperty("ymin").GetDouble(),
                XMax = bbox.GetProperty("xmax").GetDouble(),
                YMax = bbox.GetProperty("ymax").GetDouble(),
                Polygons = ParsePolygons(root),
                Pins = ParsePins(root),
                PolygonWarning = root.TryGetProperty("polygon_warning", out var pw)
                    ? pw.GetString() : null,
                Source = root.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null,
            };
        }
        catch (Exception ex)
        {
            return NazcaPreviewResult.Fail($"Failed to parse preview output: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the stdout from the bottom up and returns the first line that
    /// parses as a JSON object. Nazca's logging chatter ("INFO : ...") never
    /// produces a JSON-shaped line, so the last line that does is our payload.
    /// </summary>
    private static string? ExtractTrailingJsonLine(string stdout)
    {
        var lines = stdout.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.StartsWith('{')) continue;
            try
            {
                using var _ = JsonDocument.Parse(trimmed);
                return trimmed;
            }
            catch (JsonException)
            {
                // Not valid JSON — keep looking upwards
            }
        }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static List<NazcaPreviewPolygon> ParsePolygons(JsonElement root)
    {
        var list = new List<NazcaPreviewPolygon>();
        if (!root.TryGetProperty("polygons", out var arr)) return list;
        foreach (var poly in arr.EnumerateArray())
        {
            var layer = poly.TryGetProperty("layer", out var lp) ? lp.GetInt32() : 0;
            var verts = new List<(double, double)>();
            if (poly.TryGetProperty("vertices", out var va))
                foreach (var v in va.EnumerateArray())
                {
                    var c = v.EnumerateArray().ToArray();
                    if (c.Length >= 2) verts.Add((c[0].GetDouble(), c[1].GetDouble()));
                }
            list.Add(new NazcaPreviewPolygon { Layer = layer, Vertices = verts });
        }
        return list;
    }

    private static List<NazcaPreviewPin> ParsePins(JsonElement root)
    {
        var list = new List<NazcaPreviewPin>();
        if (!root.TryGetProperty("pins", out var arr)) return list;
        foreach (var p in arr.EnumerateArray())
        {
            list.Add(new NazcaPreviewPin
            {
                Name   = p.TryGetProperty("name",   out var n)  ? n.GetString() ?? "" : "",
                X      = p.TryGetProperty("x",      out var xp) ? xp.GetDouble() : 0,
                Y      = p.TryGetProperty("y",      out var yp) ? yp.GetDouble() : 0,
                Angle  = p.TryGetProperty("angle",  out var ap) ? ap.GetDouble() : 0,
                StubX1 = p.TryGetProperty("stubX1", out var sx) ? sx.GetDouble() : 0,
                StubY1 = p.TryGetProperty("stubY1", out var sy) ? sy.GetDouble() : 0,
            });
        }
        return list;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}

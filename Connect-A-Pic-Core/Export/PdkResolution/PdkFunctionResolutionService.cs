using System.Diagnostics;
using System.Text.Json;

namespace CAP_Core.Export.PdkResolution;

/// <summary>
/// Invokes <c>scripts/list_pdk_resolution.py</c> to batch-verify that PDK
/// <c>nazcaFunction</c> strings resolve against the installed Python packages
/// (issue #515). Never throws — returns a failure report instead.
/// </summary>
public class PdkFunctionResolutionService
{
    /// <summary>Default subprocess timeout — a batch touches many imports, allow generous time.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly ProcessLaunchFactory _launchFactory;
    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes the service.</summary>
    /// <param name="pythonExecutable">Path to a Python 3 executable with nazca installed.</param>
    /// <param name="scriptPath">Absolute path to list_pdk_resolution.py.</param>
    /// <param name="timeout">Optional subprocess timeout.</param>
    /// <param name="launchFactory">Factory used to build platform-aware process start info.</param>
    public PdkFunctionResolutionService(
        string pythonExecutable, string scriptPath,
        TimeSpan? timeout = null, ProcessLaunchFactory? launchFactory = null)
    {
        _pythonExecutable = pythonExecutable ?? throw new ArgumentNullException(nameof(pythonExecutable));
        _scriptPath = scriptPath ?? throw new ArgumentNullException(nameof(scriptPath));
        _timeout = timeout ?? DefaultTimeout;
        _launchFactory = launchFactory ?? ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Resolves each entry against the installed Python packages.
    /// Entries are written to a temp JSON file and passed to the helper script.
    /// </summary>
    public virtual async Task<PdkResolutionReport> ResolveAsync(
        IReadOnlyList<PdkResolutionEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return new PdkResolutionReport { Success = true };
        if (!File.Exists(_scriptPath))
            return PdkResolutionReport.Fail($"Resolution script not found: {_scriptPath}");

        var inputFile = Path.Combine(Path.GetTempPath(), $"lunima_pdk_resolution_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(inputFile, SerializeEntries(entries), ct);
            return await RunProcessAsync(inputFile, ct);
        }
        catch (Exception ex)
        {
            return PdkResolutionReport.Fail($"Unexpected error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(inputFile)) File.Delete(inputFile); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Serializes request entries into the script's input JSON shape.</summary>
    internal static string SerializeEntries(IReadOnlyList<PdkResolutionEntry> entries)
        => JsonSerializer.Serialize(entries.Select(e => new
        {
            name = e.Name,
            module = e.Module,
            function = e.Function,
            backend = e.Backend
        }));

    private async Task<PdkResolutionReport> RunProcessAsync(string inputFile, CancellationToken ct)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(_scriptPath);
            var arguments = new[] { _scriptPath, "--input", inputFile };
            // PYTHONSAFEPATH: stray sibling files next to the script must not
            // shadow stdlib/Nazca modules (see PythonModuleShadowing).
            if (!_launchFactory.TryBuild(_pythonExecutable, arguments, workingDir,
                    PythonModuleShadowing.SafePathEnvironment, out var psi, out var launchError))
                return PdkResolutionReport.Fail($"Could not start Python '{_pythonExecutable}': {launchError}");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi);
            if (process == null)
                return PdkResolutionReport.Fail($"Could not start Python '{_pythonExecutable}'.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeoutTask = Task.Delay(_timeout, ct);
            var completed = await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), timeoutTask);
            if (completed == timeoutTask || ct.IsCancellationRequested)
            {
                TryKill(process);
                return ct.IsCancellationRequested
                    ? PdkResolutionReport.Fail("Operation was cancelled.")
                    : PdkResolutionReport.Fail($"Resolution script timed out after {_timeout.TotalSeconds:F0}s.");
            }

            await process.WaitForExitAsync(ct);
            return ParseOutput(await stdoutTask, await stderrTask);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return PdkResolutionReport.Fail($"Could not start Python '{_pythonExecutable}': {ex.Message}");
        }
        catch (Exception ex)
        {
            return PdkResolutionReport.Fail($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the JSON document the helper script writes on stdout. Exposed
    /// as internal so unit tests can exercise the JSON path without spawning
    /// a real subprocess (the CI Linux box may lack nazca).
    /// </summary>
    internal static PdkResolutionReport ParseOutput(string stdout, string? stderr = null)
    {
        var jsonLine = ExtractTrailingJsonLine(stdout);
        if (jsonLine == null)
        {
            // Surface the interpreter's own error (e.g. a traceback printed to stderr before
            // any JSON) instead of a bare "no output" — otherwise the failure is undebuggable
            // from the UI (#515 review).
            var detail = LastLines(stderr, 5);
            return PdkResolutionReport.Fail(string.IsNullOrEmpty(detail)
                ? "Resolution script produced no JSON output."
                : $"Resolution script produced no JSON output. Python error:\n{detail}");
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var sp) && !sp.GetBoolean())
            {
                var msg = root.TryGetProperty("error", out var ep) ? ep.GetString() : null;
                return PdkResolutionReport.Fail(msg ?? "Unknown error");
            }

            var results = new List<PdkResolutionResult>();
            if (root.TryGetProperty("results", out var arr))
                foreach (var item in arr.EnumerateArray())
                    results.Add(ParseResult(item));

            return new PdkResolutionReport { Success = true, Results = results };
        }
        catch (Exception ex)
        {
            return PdkResolutionReport.Fail($"Failed to parse resolution output: {ex.Message}");
        }
    }

    private static PdkResolutionResult ParseResult(JsonElement item) => new()
    {
        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        Status = ParseStatus(item.TryGetProperty("status", out var s) ? s.GetString() : null),
        Kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
        Message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""
    };

    private static PdkResolutionStatus ParseStatus(string? status) => status switch
    {
        "ok" => PdkResolutionStatus.Ok,
        "warning" => PdkResolutionStatus.Warning,
        _ => PdkResolutionStatus.Error
    };

    /// <summary>Returns the last <paramref name="count"/> non-empty lines of text, trimmed.</summary>
    private static string LastLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - count)));
    }

    /// <summary>
    /// Walks the stdout from the bottom up and returns the first line that
    /// parses as JSON — Nazca chatter that bypasses the script's stdout
    /// redirect never produces a JSON-shaped line.
    /// </summary>
    private static string? ExtractTrailingJsonLine(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith('{'))
                continue;
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

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}

using CAP.Avalonia.Services;
using CAP_Core.Export;

namespace UnitTests.Integration;

/// <summary>
/// Environment resolution shared by the alignment test matrix
/// (<see cref="GdsExportAlignmentTests"/>).
/// </summary>
internal static class GdsAlignmentTestSetup
{
    /// <summary>Resolves (nazca-capable python, preview script path), or nulls to skip.</summary>
    public static async Task<(string? Python, string? Script)> ResolveEnvironmentAsync()
    {
        // Opt-in gate: this alignment matrix spawns real python + GDS tooling. It runs only when
        // the workflow points at a python (CI sets LUNIMA_TEST_PYTHON3); a plain local run skips.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUNIMA_TEST_PYTHON3")))
            return (null, null);
        var python = await new PythonDiscoveryService().FindFirstNazcaPythonPathAsync();
        return (python, FindRealPreviewScript());
    }

    /// <summary>
    /// Walks up from the test assembly to the repo root and returns the path to
    /// <c>scripts/render_component_preview.py</c>, or null if not found. Shared by the
    /// nazca-preview tests so the lookup lives in one place (issue #565).
    /// </summary>
    internal static string? FindRealPreviewScript()
    {
        const string scriptName = "render_component_preview.py";
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(GdsAlignmentTestSetup).Assembly.Location)!);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "scripts", scriptName);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}

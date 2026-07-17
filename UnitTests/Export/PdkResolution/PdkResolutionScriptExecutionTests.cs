using CAP_Core.Export.PdkResolution;
using Shouldly;
using Xunit;

namespace UnitTests.Export.PdkResolution;

/// <summary>
/// End-to-end: runs the real <c>list_pdk_resolution.py</c> via the production
/// <see cref="PdkFunctionResolutionService"/> against a Python env that has
/// <c>cspdk</c>, verifying a gdsfactory-native cell resolves through the export's
/// activate-PDK-then-get_component path (issue #515 review). Skips silently when no
/// cspdk-capable interpreter is present (e.g. CI), like the other script-execution tests.
/// </summary>
[Trait("Category", "Slow")]
public class PdkResolutionScriptExecutionTests
{
    [SkippableFact]
    public async Task GdsFactoryCornerStoneCells_ResolveViaActivePdk_MissingCellFails()
    {
        var python = FindCspdkPython();
        var script = FindResolutionScript();
        // Real skip (not a silent pass) so CI shows the gap explicitly (#515 review).
        Skip.If(python == null || script == null,
            "No cspdk-capable Python env found (expected on CI); covered on a dev machine with cspdk.");

        var service = new PdkFunctionResolutionService(python, script);
        var report = await service.ResolveAsync(new[]
        {
            new PdkResolutionEntry { Name = "Mmi1x2", Module = "cspdk.sin300", Function = "mmi1x2", Backend = "gdsfactory" },
            new PdkResolutionEntry { Name = "Bogus", Module = "cspdk.sin300", Function = "does_not_exist", Backend = "gdsfactory" },
        });

        report.Success.ShouldBeTrue(report.Error);
        report.Results.Count.ShouldBe(2);
        report.Results[0].Status.ShouldBe(PdkResolutionStatus.Ok);   // real cspdk cell → resolves
        report.Results[1].Status.ShouldBe(PdkResolutionStatus.Error); // unknown cell → dead reference
    }

    /// <summary>Walks up from the test output dir to the repo's scripts/list_pdk_resolution.py.</summary>
    private static string? FindResolutionScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "list_pdk_resolution.py");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Locates a Lunima managed env (%LOCALAPPDATA%/Lunima/envs/*) that has cspdk installed.</summary>
    private static string? FindCspdkPython()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (!Directory.Exists(envs))
            return null;

        foreach (var root in Directory.GetDirectories(envs))
        {
            if (!HasCspdk(root)) continue;
            foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
            {
                var py = Path.Combine(root, rel);
                if (File.Exists(py)) return py;
            }
        }
        return null;
    }

    private static bool HasCspdk(string envRoot)
    {
        if (Directory.Exists(Path.Combine(envRoot, "Lib", "site-packages", "cspdk")))
            return true;   // Windows venv layout
        var lib = Path.Combine(envRoot, "lib");
        return Directory.Exists(lib)
            && Directory.GetDirectories(lib).Any(d => Directory.Exists(Path.Combine(d, "site-packages", "cspdk")));
    }
}

using CAP_Core.Export.PdkResolution;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Export.PdkResolution;

/// <summary>
/// End-to-end: runs the real <c>list_pdk_resolution.py</c> via the production
/// <see cref="PdkFunctionResolutionService"/> against every component the bundled
/// <c>demo-pdk.json</c> ships, the same way <c>PdkResolutionCheckViewModel</c> builds
/// its batch (issue: "Probe Pad" referenced a non-existent <c>nazca.demofab.probe_pad</c>
/// cell — field test found the PDK Python check reporting it red). Skips silently when
/// no nazca-capable interpreter is present (e.g. CI), like the other script-execution
/// tests in this folder.
/// </summary>
[Trait("Category", "Slow")]
public class DemoPdkResolutionEndToEndTests
{
    [SkippableFact]
    public async Task DemoPdk_AllComponents_ResolveAgainstInstalledNazca()
    {
        var python = FindNazcaPython();
        var script = FindResolutionScript();
        var pdkPath = FindDemoPdkPath();
        // Real skip (not a silent pass) so CI shows the gap explicitly, matching the
        // sibling cspdk test's convention.
        Skip.If(python == null || script == null || pdkPath == null,
            "No nazca-capable Python env found (expected on CI); covered on a dev machine with nazca installed.");

        var draft = new PdkLoader().LoadFromFile(pdkPath!);
        var entries = draft.Components
            .Select(c =>
            {
                var (module, function) = NazcaFunctionPath.Split(c.NazcaFunction);
                return new PdkResolutionEntry { Name = c.Name, Module = module, Function = function };
            })
            .ToList();

        var service = new PdkFunctionResolutionService(python!, script!);
        var report = await service.ResolveAsync(entries);

        report.Success.ShouldBeTrue(report.Error);
        var failures = report.Results.Where(r => r.Status == PdkResolutionStatus.Error).ToList();
        failures.ShouldBeEmpty(
            "Demo PDK components with a dead nazcaFunction reference: " +
            string.Join(", ", failures.Select(f => $"{f.Name} ({f.Message})")));
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

    /// <summary>Walks up from the test output dir to the repo's bundled demo-pdk.json.</summary>
    private static string? FindDemoPdkPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "CAP-DataAccess", "PDKs", "demo-pdk.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Locates a Lunima managed env (%LOCALAPPDATA%/Lunima/envs/*) that has nazca installed.</summary>
    private static string? FindNazcaPython()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (!Directory.Exists(envs))
            return null;

        foreach (var root in Directory.GetDirectories(envs))
        {
            if (!HasNazca(root)) continue;
            foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
            {
                var py = Path.Combine(root, rel);
                if (File.Exists(py)) return py;
            }
        }
        return null;
    }

    private static bool HasNazca(string envRoot)
    {
        if (Directory.Exists(Path.Combine(envRoot, "Lib", "site-packages", "nazca")))
            return true; // Windows venv layout
        var lib = Path.Combine(envRoot, "lib");
        return Directory.Exists(lib)
            && Directory.GetDirectories(lib).Any(d => Directory.Exists(Path.Combine(d, "site-packages", "nazca")));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Enforces the cross-platform launching rule from CLAUDE.md §1.2: external processes must be
/// launched through the shared abstractions — <c>CAP_Core.Export.ProcessLaunchFactory</c> for
/// tools (python, docker, …) and <c>CAP.Avalonia.Services.IUrlLauncher</c> /
/// <c>PlatformShellLauncher</c> for opening URLs/files — never by constructing a
/// <see cref="System.Diagnostics.ProcessStartInfo"/> inline or hardcoding an executable name.
///
/// A bare <c>new ProcessStartInfo { FileName = "python" }</c> or <c>Process.Start("explorer.exe", …)</c>
/// bakes in one platform's PATH/command assumptions, which is the most common way the macOS build
/// silently falls behind Windows (or vice versa). This test fails the build the moment a new such
/// site appears, so parity is caught in CI rather than at release time.
/// </summary>
public class CrossPlatformProcessLaunchTests
{
    /// <summary>
    /// Production project roots to scan, relative to the repo root. UnitTests is deliberately
    /// excluded: test helpers may probe interpreters directly in the controlled CI environment.
    /// </summary>
    private static readonly string[] ProductionRoots =
    {
        "CAP.Avalonia",
        "CAP.Browser",
        "CAP.Desktop",
        "Connect-A-Pic-Core",
        "CAP-DataAccess",
        "CAP_Contracts",
    };

    /// <summary>
    /// Files permitted to build a <see cref="System.Diagnostics.ProcessStartInfo"/> directly
    /// (repo-relative, forward slashes). Two tiers:
    /// <list type="number">
    ///   <item><b>Sanctioned abstractions</b> — the approved launch points; all other code routes here.</item>
    ///   <item><b>Grandfathered platform-aware launchers</b> — pre-existing, not yet routed through the
    ///   factory. This list should SHRINK, never grow: new launching code must use the abstractions
    ///   (CLAUDE.md §1.2), not be added here. Adding an entry is a deliberate, review-visible act.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> AllowedDirectLaunchFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // 1. Sanctioned abstractions
        "Connect-A-Pic-Core/Export/ProcessLaunchFactory.cs",
        "CAP.Avalonia/Services/PlatformShellLauncher.cs",
        // The single sanctioned launcher for the self-update feature: it must start a DETACHED
        // helper that outlives the quitting app, which a ProcessLaunchFactory (awaited) launch can't.
        "CAP.Avalonia/Services/Update/DetachedUpdaterLauncher.cs",
        // 2. Grandfathered platform-aware launchers (pre-existing — do NOT add new entries here)
        "CAP.Avalonia/Services/PythonResolution.cs",
        "CAP.Avalonia/Services/Solvers/PythonModeSolverService.cs",
    };

    [Fact]
    public void ExternalProcessLaunches_GoThroughTheCrossPlatformAbstractions()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        foreach (var root in ProductionRoots)
        {
            var absoluteRoot = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absoluteRoot))
                continue;

            foreach (var file in Directory.GetFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                    continue;

                CollectViolations(file, repoRoot, violations);
            }
        }

        violations.ShouldBeEmpty(
            "\nCross-platform launching rule (CLAUDE.md §1.2) violated — these sites launch a process\n" +
            "without the shared abstractions, which is how one OS silently drifts from the others:\n\n" +
            string.Join("\n", violations.Select(v => $"  ✗ {v}")) +
            "\n\nUse CAP_Core.Export.ProcessLaunchFactory.TryBuild(...) for external tools, or\n" +
            "CAP.Avalonia.Services.IUrlLauncher / PlatformShellLauncher to open URLs/files/folders.\n" +
            "A genuinely new platform-aware launcher may be added to\n" +
            "CrossPlatformProcessLaunchTests.AllowedDirectLaunchFiles — but justify it in review.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CollectViolations(string filePath, string repoRoot, List<string> violations)
    {
        var relativePath = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
        var allowedDirectConstruction = AllowedDirectLaunchFiles.Contains(relativePath);
        var lines = File.ReadAllLines(filePath);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Skip comment / XML-doc lines so a `<see cref="Process.Start"/>` reference is not flagged.
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                continue;

            var lineNumber = i + 1;

            // (a) Hardcoded-executable form — never cross-platform. Banned everywhere
            //     (Process.Start(psi) with a variable is fine and is NOT matched).
            if (line.Contains("Process.Start(\""))
            {
                violations.Add(
                    $"{relativePath}:{lineNumber}  Process.Start(\"…\") hardcodes an executable — " +
                    "open URLs/files via IUrlLauncher, run tools via ProcessLaunchFactory.");
            }

            // (b) Direct ProcessStartInfo construction outside the sanctioned/grandfathered set —
            //     bypasses the factory's per-OS executable resolution + PATH augmentation.
            if (!allowedDirectConstruction && line.Contains("new ProcessStartInfo"))
            {
                violations.Add(
                    $"{relativePath}:{lineNumber}  new ProcessStartInfo — build it via " +
                    "ProcessLaunchFactory.TryBuild(...) (tools) or use IUrlLauncher (URLs/files).");
            }
        }
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            // In a normal clone .git is a directory; in a git worktree it is a file.
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root (.git directory or file).");
    }
}

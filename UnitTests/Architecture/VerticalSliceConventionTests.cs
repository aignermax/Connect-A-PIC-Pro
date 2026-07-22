using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Enforces the vertical-slice convention documented in CLAUDE.md §1.1.
/// Each feature's code should only reference its own namespace or the shared kernel.
/// Prevents feature A from reaching into feature B's internals as the codebase grows.
/// </summary>
public class VerticalSliceConventionTests
{
    /// <summary>
    /// Folders belonging to each feature, relative to the repository root.
    /// Add the feature's folder paths across all layers (Core, ViewModels, Tests).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FeatureFolders =
        new Dictionary<string, string[]>
        {
            ["Analysis"] = new[]
            {
                Path.Combine("Connect-A-Pic-Core", "Analysis"),
                Path.Combine("CAP.Avalonia", "ViewModels", "Analysis"),
                Path.Combine("UnitTests", "Analysis"),
            },
            ["Export"] = new[]
            {
                Path.Combine("Connect-A-Pic-Core", "Export"),
                Path.Combine("CAP.Avalonia", "ViewModels", "Export"),
                Path.Combine("UnitTests", "Export"),
            },
            ["ComponentRegistry"] = new[]
            {
                Path.Combine("Connect-A-Pic-Core", "ComponentRegistry"),
                Path.Combine("CAP.Avalonia", "ViewModels", "ComponentRegistry"),
                Path.Combine("UnitTests", "ComponentRegistry"),
            },
        };

    /// <summary>
    /// Namespace prefixes of each feature. Files inside a feature folder must not
    /// import the namespace of a <em>sibling</em> feature (any entry other than their own).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FeatureNamespaces =
        new Dictionary<string, string[]>
        {
            ["Analysis"] = new[] { "CAP_Core.Analysis", "CAP.Avalonia.ViewModels.Analysis" },
            ["Export"]   = new[] { "CAP_Core.Export",   "CAP.Avalonia.ViewModels.Export"   },
            ["ComponentRegistry"] = new[]
            {
                "CAP_Core.ComponentRegistry",
                "CAP.Avalonia.ViewModels.ComponentRegistry",
            },
        };

    /// <summary>
    /// Namespace prefixes that every feature is allowed to import — the shared kernel.
    /// Platform and framework namespaces are implicitly allowed via the "System.", "Avalonia." etc.
    /// entries. Add domain-level shared types here as the codebase grows.
    /// </summary>
    private static readonly string[] SharedKernelPrefixes = new[]
    {
        // Framework / tooling
        "System.", "Microsoft.", "Avalonia.", "CommunityToolkit.", "Moq.", "Xunit.", "Shouldly.",
        // Domain shared kernel
        "CAP_Core.Components",
        "CAP_Core.Helpers",
        "CAP_Core.Grid",
        "CAP_Core.Tiles",
        "CAP_Core.ExternalPorts",
        "CAP_Core.Routing",
        "CAP_Core.Resources",
        "CAP_Core.LightCalculation",
        "CAP_Contracts",
        // Bare root namespaces (ErrorConsoleService, SimulationService, etc.)
        "CAP_Core;",
        // UI shared infrastructure
        "CAP.Avalonia.Services",
        "CAP.Avalonia.ViewModels.Canvas",
        "CAP.Avalonia.ViewModels.Panels",
        "CAP.Avalonia.ViewModels.Converters",
        "CAP.Avalonia.Commands",
        "CAP.Avalonia;",
        // Data access layer (not feature-specific)
        "CAP_DataAccess",
    };

    /// <summary>
    /// For each enumerated feature, verifies that no source file inside the feature's
    /// folders contains a <c>using</c> directive that points to a sibling feature's namespace.
    /// Cross-feature reach-ins break the vertical-slice convention and make refactoring harder.
    /// </summary>
    [Theory]
    [InlineData("Analysis")]
    [InlineData("Export")]
    [InlineData("ComponentRegistry")]
    public void Feature_OnlyReferencesItsOwnNamespaceOrSharedKernel(string featureName)
    {
        var repoRoot = FindRepoRoot();
        var siblingNamespaces = FeatureNamespaces
            .Where(kv => kv.Key != featureName)
            .SelectMany(kv => kv.Value)
            .ToArray();

        var violations = new List<string>();

        foreach (var relativeFolder in FeatureFolders[featureName])
        {
            var absoluteFolder = Path.Combine(repoRoot, relativeFolder);
            if (!Directory.Exists(absoluteFolder))
                continue;

            foreach (var file in Directory.GetFiles(absoluteFolder, "*.cs", SearchOption.AllDirectories))
            {
                CollectViolations(file, repoRoot, siblingNamespaces, violations);
            }
        }

        violations.ShouldBeEmpty(
            $"\nFeature '{featureName}' has cross-feature reach-ins into sibling feature namespaces.\n\n" +
            string.Join("\n", violations.Select(v => $"  ✗ {v}")) +
            "\n\nEach feature should only reference its own namespace or the shared kernel.\n" +
            "See CLAUDE.md §1.1 for the vertical-slice convention and shared-kernel allow-list.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CollectViolations(
        string filePath,
        string repoRoot,
        string[] siblingNamespaces,
        List<string> violations)
    {
        var lines = File.ReadAllLines(filePath);
        var relativePath = Path.GetRelativePath(repoRoot, filePath);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("using "))
                continue;

            var afterUsing = trimmed[6..].TrimEnd(';', ' ');

            // Skip alias imports (e.g., "using Alias = Some.Namespace") — the target
            // is visible in the type reference, not the using statement.
            if (afterUsing.Contains(" = "))
                continue;

            // Skip global using and static using modifiers
            if (afterUsing.StartsWith("global ") || afterUsing.StartsWith("static "))
                continue;

            if (siblingNamespaces.Any(ns => afterUsing.StartsWith(ns)))
            {
                violations.Add($"{relativePath}: `using {afterUsing}`");
            }
        }
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

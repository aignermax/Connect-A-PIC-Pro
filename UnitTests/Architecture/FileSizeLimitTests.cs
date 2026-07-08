using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Enforces file size limits from CLAUDE.md architecture guidelines.
/// Prevents code debt from accumulating via oversized files.
/// Only checks production code (UnitTests/ folder is excluded — test suites grow naturally).
/// </summary>
public class FileSizeLimitTests
{
    private const int SoftLimitLines = 300;
    private const int HardLimitLines = 500;
    private const int MaxGrandfatheredFiles = 11;

    /// <summary>
    /// Existing large files that predate this rule.
    /// Each entry must include a line comment with line count and context.
    /// Do NOT add new files here without an issue number tracking refactoring.
    /// </summary>
    private static readonly HashSet<string> GrandfatheredFiles = new()
    {
        "FileOperationsViewModel.cs",       // 1031 lines - Issue #433 tracks extraction
        "SimpleNazcaExporter.cs",           // 826 lines  - GDS export monolith, refactor planned
        "ComponentGroup.cs",                // 796 lines  - Core group model, refactor planned
        "PathfindingGrid.cs",               // 771 lines  - A* grid state, refactor planned
        "CanvasInteractionViewModel.cs",    // 765 lines  - Canvas input handler, refactor planned
        "MainViewModel.cs",                 // 642 lines  - Coordinator ViewModel, acceptable
        "ComponentGroupRenderer.cs",        // 597 lines  - Rendering logic, refactor planned
        "WaveguideConnectionManager.cs",    // 592 lines  - Connection tracking, refactor planned
        "GroupTemplateSerializer.cs",       // 559 lines  - Serialization, refactor planned
        "MainWindow.axaml.cs",              // 559 lines  - View code-behind, grew with Issue #510 component settings wiring
    };

    /// <summary>
    /// Fails the build if any new production file exceeds 500 lines.
    /// Grandfathered files (see <see cref="GrandfatheredFiles"/>) are exempt.
    /// </summary>
    [Fact]
    public void NoProductionFileExceedsHardLimit()
    {
        var repoRoot = GetRepositoryRoot();
        var oversizedFiles = new List<(string FilePath, int LineCount)>();

        foreach (var file in GetProductionCsFiles(repoRoot))
        {
            var lineCount = File.ReadAllLines(file).Length;
            var fileName = Path.GetFileName(file);

            if (lineCount > HardLimitLines && !GrandfatheredFiles.Contains(fileName))
            {
                oversizedFiles.Add((file, lineCount));
            }
        }

        if (oversizedFiles.Any())
        {
            Assert.Fail(BuildFailureReport(oversizedFiles, repoRoot));
        }
    }

    /// <summary>
    /// Logs a warning (via console) for production files exceeding 300 lines.
    /// Does not fail the build — acts as an early-warning system.
    /// </summary>
    [Fact]
    public void ProductionFilesApproachingSoftLimit_AreLogged()
    {
        var repoRoot = GetRepositoryRoot();
        var warningFiles = new List<(string FilePath, int LineCount)>();

        foreach (var file in GetProductionCsFiles(repoRoot))
        {
            var lineCount = File.ReadAllLines(file).Length;
            var fileName = Path.GetFileName(file);

            if (lineCount > SoftLimitLines && !GrandfatheredFiles.Contains(fileName))
            {
                warningFiles.Add((file, lineCount));
            }
        }

        if (warningFiles.Any())
        {
            Console.WriteLine(BuildWarningReport(warningFiles, repoRoot));
        }

        // Always passes — this is a visibility test, not a gate
        true.ShouldBeTrue();
    }

    /// <summary>
    /// Ensures the grandfathered list stays small.
    /// A growing list signals architectural debt is accumulating instead of being addressed.
    /// </summary>
    [Fact]
    public void GrandfatheredList_DoesNotExceedMaximumSize()
    {
        GrandfatheredFiles.Count.ShouldBeLessThan(
            MaxGrandfatheredFiles,
            $"Too many grandfathered files ({GrandfatheredFiles.Count}). " +
            "Refactor existing large files before adding new exceptions.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> GetProductionCsFiles(string repoRoot)
    {
        return Directory
            .GetFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            // Local-only agent worktrees mirror the main tree and skew the
            // limit check; they're never committed (see .gitignore).
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "UnitTests" + Path.DirectorySeparatorChar));
    }

    private string BuildFailureReport(List<(string FilePath, int LineCount)> files, string repoRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\n❌ HARD LIMIT EXCEEDED: {files.Count} file(s) exceed {HardLimitLines} lines\n");
        sb.AppendLine("These files must be refactored before merging:\n");

        foreach (var (path, lines) in files.OrderByDescending(f => f.LineCount))
        {
            var relativePath = Path.GetRelativePath(repoRoot, path);
            sb.AppendLine($"  - {relativePath}: {lines} lines (over limit by {lines - HardLimitLines})");
        }

        sb.AppendLine("\nOptions:");
        sb.AppendLine("  1. Split into smaller classes (preferred)");
        sb.AppendLine($"  2. Add to GrandfatheredFiles in {nameof(FileSizeLimitTests)}.cs with an issue number");
        sb.AppendLine("\nSee CLAUDE.md for file size guidelines.");

        return sb.ToString();
    }

    private string BuildWarningReport(List<(string FilePath, int LineCount)> files, string repoRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"\n⚠️  SOFT LIMIT WARNING: {files.Count} file(s) exceed {SoftLimitLines} lines\n");

        foreach (var (path, lines) in files.OrderByDescending(f => f.LineCount).Take(10))
        {
            var relativePath = Path.GetRelativePath(repoRoot, path);
            sb.AppendLine($"  - {relativePath}: {lines} lines");
        }

        if (files.Count > 10)
            sb.AppendLine($"  ... and {files.Count - 10} more");

        sb.AppendLine("\nConsider splitting files approaching 500 lines.");

        return sb.ToString();
    }

    private static string GetRepositoryRoot()
    {
        // `.git` is a directory in a normal checkout but a *file* in a git worktree.
        var dir = Directory.GetCurrentDirectory();
        while (dir != null
               && !Directory.Exists(Path.Combine(dir, ".git"))
               && !File.Exists(Path.Combine(dir, ".git")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir ?? throw new InvalidOperationException("Could not find repository root (.git directory).");
    }
}

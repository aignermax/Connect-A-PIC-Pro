using System.Collections.ObjectModel;
using System.Diagnostics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Performance probe / regression guard for the large-GDS import hang (issue
/// #811): generates a production-scale library with <see cref="GdsImportBenchmark"/>
/// (2500 chained instances + 3000 top-cell route polygons + 100 abutment pairs),
/// times every import stage end-to-end — parse, hierarchy import, service total
/// (parse + import + persist + register), plan build, canvas placement — and
/// asserts the whole flow stays within a generous budget so CI won't flake.
/// <para>
/// The scale defaults to the issue's target shape; set GDS_PROBE_CHAINED /
/// GDS_PROBE_ABUTMENT to shrink it for ad-hoc local profiling runs.
/// </para>
/// </summary>
[Trait("Category", "Slow")]
public class GdsImportPerformanceProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsprobe-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gdsprobe-prefs-{Guid.NewGuid():N}.json");

    private static int ChainedInstances =>
        int.TryParse(Environment.GetEnvironmentVariable("GDS_PROBE_CHAINED"), out int v) ? v : 2500;

    private static int AbutmentPairs =>
        int.TryParse(Environment.GetEnvironmentVariable("GDS_PROBE_ABUTMENT"), out int v) ? v : 100;

    /// <summary>Generous whole-flow budget: the pre-fix hang was minutes, the target is seconds.</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(60);

    public GdsImportPerformanceProbeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public async Task LargeProductionFile_ImportsAndPlacesWithinBudget()
    {
        var timings = new List<(string Stage, TimeSpan Elapsed)>();
        var watch = new Stopwatch();

        // ── Fixture generation (not part of the import cost) ────────────────
        watch.Start();
        byte[] bytes = GdsImportBenchmark.CreateLibrary(
            chainedInstances: ChainedInstances, abutmentPairs: AbutmentPairs);
        watch.Stop();
        timings.Add(("generate fixture", watch.Elapsed));

        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "benchmark.gds");
        await File.WriteAllBytesAsync(gdsPath, bytes);
        _output.WriteLine($"fixture: {bytes.Length / 1e6:0.0} MB, {ChainedInstances} chained + " +
                          $"{2 * AbutmentPairs} abutment instances");

        // ── Stage: parse ─────────────────────────────────────────────────────
        GdsLibrary library;
        using (Stage(timings, "parse (GdsReader)"))
        {
            await using var stream = File.OpenRead(gdsPath);
            library = await new GdsReader().ReadAsync(stream);
        }

        // ── Stage: hierarchy import (flatten, pins, matching, abutment) ─────
        GdsCircuitImport circuit;
        using (Stage(timings, "hierarchy import"))
        {
            circuit = await GdsHierarchyImporter.ImportAsync(
                library, "TOP", new GdsHierarchyImportOptions());
        }

        // ── Stage: full service import (re-parses; persists + registers) ────
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(
            new UserPdkStore(Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader()),
            () => sink.Templates, sink.Register);
        GdsImportOutcome outcome;
        using (Stage(timings, "service ImportAsync total"))
        {
            outcome = await service.ImportAsync(gdsPath, "TOP");
        }

        // ── Stage: placement plan build ──────────────────────────────────────
        GdsPlacementPlan plan;
        using (Stage(timings, "placement plan build"))
        {
            plan = GdsPlacementPlan.FromOutcome(outcome);
        }

        // ── Stage: canvas placement + connect + group ────────────────────────
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => sink.Templates);
        GdsPlacementReport report;
        using (Stage(timings, "executor ExecuteAsync"))
        {
            report = await executor.ExecuteAsync(plan);
        }

        foreach (var (stage, elapsed) in timings)
            _output.WriteLine($"{stage,-28} {elapsed.TotalMilliseconds,9:0} ms");
        double totalMs = timings.Sum(t => t.Elapsed.TotalMilliseconds);
        _output.WriteLine($"{"TOTAL",-28} {totalMs,9:0} ms");

        // Shape sanity: the fixture must actually exercise the intended paths.
        outcome.RegisteredComponents.Count.ShouldBe(ChainedInstances + 1); // + the shared abutment cell
        report.PlacedCount.ShouldBe(ChainedInstances + 2 * AbutmentPairs);
        report.RouteDerivedCount.ShouldBeGreaterThan(0);
        report.ConnectedCount.ShouldBe(report.RouteDerivedCount + AbutmentPairs);
        report.SkippedConnections.ShouldBeEmpty();
        report.GroupCreated.ShouldBeTrue();

        totalMs.ShouldBeLessThan(TotalBudget.TotalMilliseconds);
    }

    private static IDisposable Stage(List<(string, TimeSpan)> timings, string name)
    {
        var watch = Stopwatch.StartNew();
        return new OnDispose(() =>
        {
            watch.Stop();
            timings.Add((name, watch.Elapsed));
        });
    }

    private sealed class OnDispose : IDisposable
    {
        private readonly Action _action;
        public OnDispose(Action action) => _action = action;
        public void Dispose() => _action();
    }

    /// <summary>Wires the real registrar with throwaway library state (pattern from GdsImportServiceTests).</summary>
    private sealed class LibrarySink
    {
        public readonly ObservableCollection<ComponentTemplate> Templates = new();
        public readonly Action<PdkComponentDraft, string, string> Register;

        public LibrarySink(string prefsPath)
        {
            var preferences = new UserPreferencesService(prefsPath);
            var loader = new PdkLoader();
            var categories = new ObservableCollection<string>();
            var pdkManager = new PdkManagerViewModel();
            var loadedDrafts = new List<PdkDraft>();
            Register = (draft, pdkName, filePath) =>
                CustomComponentLibraryRegistrar.Register(
                    draft, pdkName, filePath, Templates, categories, pdkManager,
                    preferences, loader, loadedDrafts, () => { }, () => { });
        }
    }
}

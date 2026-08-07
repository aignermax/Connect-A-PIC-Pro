using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Regression tests for the dialog's per-run cancellation lifecycle (the
/// "GDS import failed: The CancellationTokenSource has been disposed" report):
/// cancel mid-run, then start a second run or close the dialog — a late token
/// reference (off-thread parse read, queued continuation) must never surface a
/// disposed-source exception, and the next run must work. Harness mirrors
/// <see cref="GdsImportDialogViewModelTests"/>.
/// </summary>
public class GdsImportCancellationLifecycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdscts-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gdscts-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    private static byte[] TwoWaveguideLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 10000, 0)
        .EndCell()
        .WaveguideCell("wgA")
        .WaveguideCell("wgB")
        .EndLibrary()
        .ToArray();

    private string WriteGds(byte[] content, string fileName = "circuit.gds")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class LibrarySink
    {
        public readonly ObservableCollection<ComponentTemplate> Templates = new();
        public readonly ObservableCollection<string> Categories = new();
        public readonly PdkManagerViewModel PdkManager = new();
        public readonly List<PdkDraft> LoadedDrafts = new();
        public readonly UserPreferencesService Preferences;
        public readonly Action<PdkComponentDraft, string, string> Register;

        public LibrarySink(string prefsPath)
        {
            Preferences = new UserPreferencesService(prefsPath);
            var loader = new PdkLoader();
            Register = (draft, pdkName, filePath) =>
                CustomComponentLibraryRegistrar.Register(
                    draft, pdkName, filePath, Templates, Categories, PdkManager,
                    Preferences, loader, LoadedDrafts, () => { }, () => { });
        }
    }

    private (GdsImportDialogViewModel vm, DesignCanvasViewModel canvas, LibrarySink sink) CreateDialog(
        string gdsPath, ErrorConsoleService console)
    {
        var sink = new LibrarySink(_prefsPath);
        var canvas = new DesignCanvasViewModel();
        var service = new GdsImportService(
            new UserPdkStore(Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader()),
            () => sink.Templates.ToList(), sink.Register);
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => sink.Templates.ToList());
        return (new GdsImportDialogViewModel(gdsPath, service, executor, console), canvas, sink);
    }

    private static void AssertNoDisposedSourceError(ErrorConsoleService console, GdsImportDialogViewModel vm)
    {
        console.Entries.ShouldNotContain(
            e => e.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase),
            $"a disposed cancellation source must never surface as an import failure; " +
            $"entries: {string.Join(" | ", console.Entries.Select(e => e.Message))}");
        vm.ErrorText.ShouldNotContain("disposed");
    }

    [Fact]
    public async Task NewRun_CancelsAndDisposesThePreviousCancellationSource()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), new ErrorConsoleService());
        await vm.StartAnalysisAsync();
        var first = vm.CurrentCts.ShouldNotBeNull();

        await vm.RetryAnalysisCommand.ExecuteAsync(null);

        first.IsCancellationRequested.ShouldBeTrue(
            "reset cancels BEFORE disposing: a late token registration on the old source " +
            "short-circuits on the cancelled state instead of touching a disposed source");
        Should.Throw<ObjectDisposedException>(() => _ = first.Token);
        vm.CurrentCts.ShouldNotBeNull().ShouldNotBeSameAs(first);
    }

    [Fact]
    public async Task CancelMidImport_ThenSecondImportRun_CompletesWithoutDisposedException()
    {
        var console = new ErrorConsoleService();
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);
        await vm.StartAnalysisAsync();

        var first = vm.ImportCommand.ExecuteAsync(null);
        // The cancellation source is assigned synchronously before ImportAsync's
        // first await, so this cancel deterministically lands mid-run.
        vm.CurrentCts.ShouldNotBeNull().Cancel();
        await first;

        // The second run's reset disposes the first run's source: the exact
        // moment a surviving token reference would hit the disposed source.
        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.ImportCompleted.ShouldBeTrue("the second import completes cleanly after the cancel");
        canvas.Components.ShouldHaveSingleItem();
        AssertNoDisposedSourceError(console, vm);
    }

    [Fact]
    public async Task CloseMidImport_RunUnwindsWithoutDisposedException()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);
        await vm.StartAnalysisAsync();

        var run = vm.ImportCommand.ExecuteAsync(null);
        var cts = vm.CurrentCts.ShouldNotBeNull();

        vm.OnWindowClosed(); // close mid-import: cancel + dispose + detach

        cts.IsCancellationRequested.ShouldBeTrue();
        vm.CurrentCts.ShouldBeNull();
        await run; // must unwind as a handled cancellation, never a disposed-source fault
        vm.IsBusy.ShouldBeFalse();
        vm.HasError.ShouldBeFalse(vm.ErrorText);
        AssertNoDisposedSourceError(console, vm);
    }

    [Fact]
    public async Task CloseMidAnalysis_ThenRetryAnalysis_NoDisposedException()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);

        var analysis = vm.StartAnalysisAsync();
        vm.OnWindowClosed();
        await analysis;

        // A fresh run after the close starts from a clean (null) source.
        await vm.StartAnalysisAsync();

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.AnalysisReady.ShouldBeTrue();
        AssertNoDisposedSourceError(console, vm);
    }

    [Fact]
    public async Task OnWindowClosed_CalledTwice_DoesNotThrow()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), new ErrorConsoleService());
        var run = vm.StartAnalysisAsync();

        vm.OnWindowClosed();
        Should.NotThrow(vm.OnWindowClosed);

        await run;
    }

    [Fact]
    public async Task CancelCommand_WhileBusyAfterClose_DoesNotThrow()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), new ErrorConsoleService());
        var run = vm.StartAnalysisAsync();
        vm.OnWindowClosed();
        await run;

        vm.IsBusy = true; // a late busy flag with the source already released
        Should.NotThrow(() => vm.CancelCommand.Execute(null));
        vm.IsBusy = false;
    }
}

/// <summary>GDS fixture cell builders for the cancellation lifecycle tests.</summary>
file static class GdsCancellationLifecycleTestCells
{
    /// <summary>10×4 µm gdsfactory-style waveguide (same shape as GdsImportDialogViewModelTests').</summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}

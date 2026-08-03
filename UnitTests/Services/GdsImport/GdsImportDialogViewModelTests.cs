using System.Collections.ObjectModel;
using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core;
using CAP_Contracts.Logger;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Headless tests for <see cref="GdsImportDialogViewModel"/>: analysis feedback,
/// option validation, and the end-to-end import → placement flow against a real
/// canvas (pattern and fixtures follow <see cref="GdsImportServiceTests"/>).
/// </summary>
public class GdsImportDialogViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsdlg-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gdsdlg-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>Wires the real registrar with throwaway library state (from GdsImportServiceTests).</summary>
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

    /// <summary>TOP with two abutting 10×4 µm waveguide cells (wgA → wgB), gdsfactory-style.</summary>
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

    /// <summary>
    /// Same cells as <see cref="TwoWaveguideLibrary"/> but with an 80 µm gap between
    /// wgA.out and wgB.in — the pins face each other without abutting, so only the
    /// auto-connect pass can wire them.
    /// </summary>
    private static byte[] TwoWaveguideLibraryWithGap() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 90000, 0)
        .EndCell()
        .WaveguideCell("wgA")
        .WaveguideCell("wgB")
        .EndLibrary()
        .ToArray();

    /// <summary>
    /// TOP with one normal waveguide plus a zero-length-straight stand-in
    /// ("zeroL": a port label on a point — an empty flattened bbox, like the
    /// zero-length straights gdsfactory's route_bundle inserts).
    /// </summary>
    private static byte[] ZeroGeometryLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("zeroL", 10000, 2000)
        .EndCell()
        .WaveguideCell("wgA")
        .BeginCell("zeroL")
            .Text(1, 10, "io", 0, 0)
        .EndCell()
        .EndLibrary()
        .ToArray();

    private string WriteGds(byte[] content, string fileName = "circuit.gds")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    private (GdsImportDialogViewModel vm, DesignCanvasViewModel canvas, LibrarySink sink) CreateDialog(
        string gdsPath, ErrorConsoleService? errorConsole = null)
    {
        var sink = new LibrarySink(_prefsPath);
        var canvas = new DesignCanvasViewModel();
        var service = new GdsImportService(Store(), () => sink.Templates.ToList(), sink.Register);
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => sink.Templates.ToList());
        return (new GdsImportDialogViewModel(gdsPath, service, executor, errorConsole), canvas, sink);
    }

    // ── Analysis ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAnalysisAsync_PopulatesTopCellCandidates()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        vm.CanImport.ShouldBeFalse("nothing to import before the analysis ran");
        await vm.StartAnalysisAsync();

        vm.HasError.ShouldBeFalse();
        vm.AnalysisReady.ShouldBeTrue();
        var top = vm.TopCells.ShouldHaveSingleItem();
        top.CellName.ShouldBe("TOP");
        top.DirectInstanceCount.ShouldBe(2);
        vm.SelectedTopCell.ShouldBe(top);
        vm.CanImport.ShouldBeTrue();
    }

    [Fact]
    public async Task StartAnalysisAsync_UnreadableFile_SurfacesUserReadableError()
    {
        var (vm, _, _) = CreateDialog(WriteGds(new byte[] { 1, 2, 3, 4 }, "garbage.gds"));

        await vm.StartAnalysisAsync();

        vm.HasError.ShouldBeTrue();
        vm.ErrorText.ShouldContain("could not be read as a GDS II layout");
        vm.AnalysisReady.ShouldBeFalse();
        vm.CanImport.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAnalysisAsync_MissingFile_SurfacesError()
    {
        var (vm, _, _) = CreateDialog(Path.Combine(_root, "nope.gds"));

        await vm.StartAnalysisAsync();

        vm.HasError.ShouldBeTrue();
        vm.ErrorText.ShouldContain("nope.gds");
    }

    [Fact]
    public async Task ImportCommand_IsDisabledWhileBusy()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();
        vm.ImportCommand.CanExecute(null).ShouldBeTrue();

        vm.IsBusy = true;

        vm.CanImport.ShouldBeFalse();
        vm.ImportCommand.CanExecute(null).ShouldBeFalse();
    }

    // ── Options validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("1,10", new[] { 1, 10 })]
    [InlineData(" 1 , 10 ", new[] { 1, 10 })]
    public void ParseLayerPairs_SinglePair_Parses(string text, int[] expected)
    {
        var pairs = GdsImportDialogViewModel.ParseLayerPairs(text);

        pairs.ShouldNotBeNull();
        pairs.ShouldHaveSingleItem().ShouldBe((expected[0], expected[1]));
    }

    [Fact]
    public void ParseLayerPairs_MultiplePairs_Parses()
    {
        var pairs = GdsImportDialogViewModel.ParseLayerPairs("1,10; 2,0");

        pairs.ShouldNotBeNull();
        pairs.Count.ShouldBe(2);
        pairs[1].ShouldBe((2, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1,2,3")]
    [InlineData("1,x")]
    [InlineData("-1,10")]
    [InlineData("1,-1")]
    public void ParseLayerPairs_Malformed_ReturnsNull(string text)
    {
        GdsImportDialogViewModel.ParseLayerPairs(text).ShouldBeNull();
    }

    [Fact]
    public async Task ImportAsync_InvalidLayerSyntax_ShowsErrorWithoutImporting()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();
        vm.PortLayersText = "bogus";

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeTrue();
        vm.ErrorText.ShouldContain("bogus");
        vm.ImportCompleted.ShouldBeFalse();
        canvas.Components.ShouldBeEmpty("nothing is imported when option validation fails");
    }

    [Fact]
    public void PortLayersText_Default_ShowsGdsFactoryAndDemofabLayers()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        vm.PortLayersText.ShouldBe("1,10;501,1",
            "the field mirrors the detector default: gdsfactory port labels (1,10) plus " +
            "nazca demofab's bb_pin_text layer (501,1), which the import service unions in anyway");
        var pairs = GdsImportDialogViewModel.ParseLayerPairs(vm.PortLayersText)
            .ShouldNotBeNull("the default text must stay valid layer syntax");
        pairs.Count.ShouldBe(2);
        pairs[0].ShouldBe((1, 10));
        pairs[1].ShouldBe((501, 1));
    }

    // ── End-to-end: analyze → import → place on canvas ───────────────────────

    [Fact]
    public async Task ImportAsync_EndToEnd_PlacesGroupedCircuitOnCanvas()
    {
        var (vm, canvas, sink) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse();
        vm.ImportCompleted.ShouldBeTrue();
        vm.ResultSummaryText.ShouldContain("2");

        // Both cells were registered into the sink library and placed as a group.
        sink.Templates.Select(t => t.Name).ShouldBe(new[] { "wgA", "wgB" }, ignoreOrder: true);
        var groupVm = canvas.Components.ShouldHaveSingleItem();
        var group = groupVm.Component.ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.GroupName.ShouldBe("TOP");
        group.ChildComponents.Count.ShouldBe(2);
        group.InternalPaths.ShouldHaveSingleItem("the wgA→wgB abutment is one connection");
    }

    [Fact]
    public async Task ImportAsync_BlackBoxMode_PlacesSingleComponentWithoutGroup()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();
        vm.IsExplodeMode = false;

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        var component = canvas.Components.ShouldHaveSingleItem().Component;
        component.ShouldNotBeOfType<CAP_Core.Components.Core.ComponentGroup>(
            "black-box mode imports the top cell as one component");
        component.RotationDegrees.ShouldBe(0);
    }

    [Fact]
    public async Task AutoConnectRequested_IsExposedButFalseByDefault()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        vm.AutoConnectRequested.ShouldBeFalse(
            "auto-connect is an opt-in experimental feature; the default import changes nothing");
    }

    // ── Auto-connect (experimental) ──────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_AutoConnectOn_ConnectsFacingFreePinsAcrossTheGap()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()));
        await vm.StartAnalysisAsync();
        vm.AutoConnectRequested = true;

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse();
        vm.ImportCompleted.ShouldBeTrue();
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.Count.ShouldBe(1,
            "only the true facing pair connects: wgA.out↔wgB.in across the gap — " +
            "wgA.in and wgB.out point AWAY from each other (wrap-around) and are skipped");

        // Both outward-facing ports land in the warnings with the not-facing skip
        // reason. The marker is built through the same localized format the
        // executor uses (empty label → the name-independent suffix of the line),
        // so the assertion holds under a non-English UI culture.
        var notFacingMarker = string.Format(CultureInfo.InvariantCulture,
            LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipNotFacingFormat"),
            "", GdsPlacementExecutor.DefaultAutoConnectRadiusUm);
        vm.Warnings.Count(w => w.Contains(notFacingMarker)).ShouldBe(2,
            "wgA.in and wgB.out are both reported as not-facing skips, not connected");
    }

    [Fact]
    public async Task ImportAsync_AutoConnectOff_LeavesGappedPinsUnconnected()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()));
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.ShouldBeEmpty("the 80 µm gap is no abutment, and auto-connect is off");
    }

    [Fact]
    public async Task ImportAsync_AutoConnectRadiusBelowGap_ConnectsNothing()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()));
        await vm.StartAnalysisAsync();
        vm.AutoConnectRequested = true;
        vm.AutoConnectRadiusText = "10"; // gap is 80 µm — out of radius

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.ShouldBeEmpty("the radius field flows into the executor's pairing pass");
    }

    [Fact]
    public async Task ImportAsync_InvalidAutoConnectRadius_ShowsErrorWithoutImporting()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()));
        await vm.StartAnalysisAsync();
        vm.AutoConnectRequested = true;
        vm.AutoConnectRadiusText = "bogus";

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeTrue();
        vm.ErrorText.ShouldContain("bogus");
        vm.ImportCompleted.ShouldBeFalse();
        canvas.Components.ShouldBeEmpty("nothing is imported when option validation fails");
    }

    // ── Error-console mirroring ──────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_Success_MirrorsEveryWarningLineToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()), console);
        await vm.StartAnalysisAsync();
        vm.AutoConnectRequested = true;

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        vm.Warnings.ShouldNotBeEmpty("the gap fixture yields not-facing auto-connect skips");
        var loggedWarnings = console.Entries
            .Where(e => e.Level == LogLevel.Warn)
            .Select(e => e.Message)
            .ToList();
        foreach (var warning in vm.Warnings)
            loggedWarnings.ShouldContain(warning);
    }

    [Fact]
    public async Task ImportAsync_ZeroGeometryCell_InfoNoteShownAndMirroredViaLogInfo()
    {
        var console = new ErrorConsoleService();
        var (vm, canvas, _) = CreateDialog(WriteGds(ZeroGeometryLibrary()), console);
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        canvas.Components.ShouldHaveSingleItem("only the real cell is placed");
        var note = vm.Infos.ShouldHaveSingleItem();
        note.ShouldContain("'zeroL'");
        note.ShouldContain("1 instance(s) skipped");
        vm.Warnings.ShouldBeEmpty("the zero-geometry cascade is gone — one info note instead");

        // The note reaches the error console at INFO level — never as a warning.
        console.Entries.ShouldContain(e => e.Level == LogLevel.Info && e.Message.Contains("'zeroL'"));
        console.Entries.ShouldNotContain(e => e.Level == LogLevel.Warn && e.Message.Contains("zeroL"));
    }

    [Fact]
    public async Task ImportAsync_KnownComponentResolution_MirroredViaLogInfoNotWarning()
    {
        var console = new ErrorConsoleService();
        var (vm, _, sink) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);
        sink.Templates.Add(new ComponentTemplate
        {
            Name = "wgA",
            Category = "Test",
            PdkSource = "testpdk",
            WidthMicrometers = 10,
            HeightMicrometers = 4,
            PinDefinitions = new[]
            {
                new PinDefinition("in", 0, 2, 180),
                new PinDefinition("out", 10, 2, 0),
            },
            CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
                pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
                new List<(Guid, double)>()),
        });
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.ImportCompleted.ShouldBeTrue();
        vm.Infos.ShouldContain(i => i.Contains("resolved to existing component 'wgA'"));
        vm.Warnings.ShouldNotContain(w => w.Contains("resolved to existing component"));
        console.Entries.ShouldContain(e =>
            e.Level == LogLevel.Info && e.Message.Contains("resolved to existing component 'wgA'"));
        console.Entries.ShouldNotContain(e =>
            e.Level == LogLevel.Warn && e.Message.Contains("resolved to existing component"));
    }

    [Fact]
    public async Task StartAnalysisAsync_Failure_LogsTheErrorToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(new byte[] { 1, 2, 3, 4 }, "garbage.gds"), console);

        await vm.StartAnalysisAsync();

        vm.HasError.ShouldBeTrue();
        var entry = console.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain(vm.ErrorText);
    }

    [Fact]
    public async Task ImportAsync_InvalidLayerSyntax_LogsTheValidationErrorToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);
        await vm.StartAnalysisAsync();
        vm.PortLayersText = "bogus";

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeTrue();
        var entry = console.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain("bogus");
    }

    [Fact]
    public async Task ImportAsync_ImportRunFails_LogsTheErrorToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var gdsPath = WriteGds(TwoWaveguideLibrary());
        var (vm, _, _) = CreateDialog(gdsPath, console);
        await vm.StartAnalysisAsync();
        // The file vanishes between analysis and import — the run fails mid-flow.
        File.Delete(gdsPath);

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeTrue();
        var entry = console.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain(vm.ErrorText);
    }

    [Fact]
    public async Task ImportAsync_Cancelled_LogsThePlacedNoteToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()), console);
        await vm.StartAnalysisAsync();

        var import = vm.ImportCommand.ExecuteAsync(null);
        // Same determinism argument as ImportAsync_Cancelled_StatusNamesPlacedCountAndRemedy:
        // the cancel lands before any placement, so the logged count is 0.
        vm.CurrentCts.ShouldNotBeNull().Cancel();
        await import;

        var expectedNote = string.Format(
            LocalizationService.Instance.Translate("GdsImport.StatusCancelledAfterPlacement"), 0);
        vm.StatusText.ShouldBe(expectedNote);
        console.Entries.ShouldContain(e => e.Level == LogLevel.Warn && e.Message.Contains(expectedNote));
    }

    [Fact]
    public void DetailsInErrorConsoleHint_ResolvesAndPointsAtTheConsole()
    {
        var hint = LocalizationService.Instance.Translate("GdsImport.DetailsInErrorConsole");

        hint.ShouldNotBe("GdsImport.DetailsInErrorConsole",
            "the dialog's hint (result view + error panel) must resolve to real text in every language");
        // The hint says where the Error Console lives (bottom panel, F12) in all five languages.
        hint.ShouldContain("F12");
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_Cancelled_StatusNamesPlacedCountAndRemedy()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();

        var import = vm.ImportCommand.ExecuteAsync(null);
        // The cancellation source is assigned synchronously before ImportAsync's
        // first await, so this cancel deterministically lands before any
        // placement (the placed count in the message is 0 here; the executor
        // tests cover the mid-placement count).
        vm.CurrentCts.ShouldNotBeNull().Cancel();
        await import;

        vm.HasError.ShouldBeFalse();
        vm.ImportCompleted.ShouldBeFalse();
        vm.StatusText.ShouldBe(string.Format(
            LocalizationService.Instance.Translate("GdsImport.StatusCancelledAfterPlacement"), 0));
        vm.StatusText.ShouldNotBe(LocalizationService.Instance.Translate("GdsImport.StatusCancelled"),
            "a bare \"Cancelled.\" hides that placements may stay on the canvas");
    }

    [Fact]
    public async Task OnWindowClosed_WhileBusy_CancelsAndDisposesTheRun()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        var run = vm.StartAnalysisAsync();
        var cts = vm.CurrentCts.ShouldNotBeNull(
            "the source is assigned synchronously before the first await");

        vm.OnWindowClosed();

        cts.IsCancellationRequested.ShouldBeTrue("a close mid-run cancels the operation");
        vm.CurrentCts.ShouldBeNull("the source is released on close");
        Should.Throw<ObjectDisposedException>(() => _ = cts.Token);

        await run; // the cancellation surfaces as a caught status, never a fault
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task StartAnalysisAsync_NewRun_DisposesThePreviousCancellationSource()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        await vm.StartAnalysisAsync();
        var first = vm.CurrentCts.ShouldNotBeNull();

        await vm.RetryAnalysisCommand.ExecuteAsync(null);

        Should.Throw<ObjectDisposedException>(() => _ = first.Token);
        vm.CurrentCts.ShouldNotBeNull().ShouldNotBeSameAs(first);
    }
}

/// <summary>GDS fixture cell builders for the dialog tests (mirrors GdsImportServiceTests).</summary>
file static class GdsImportDialogTestCells
{
    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on the waveguide
    /// layer (1,0), an extent rectangle on (111,0), and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}

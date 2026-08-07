using System.Collections.ObjectModel;
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
    /// wgA.out and wgB.in — the pins face each other without abutting and without
    /// any drawn route geometry between them, so no connection may be reconstructed.
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
    /// Same cells 5 µm apart, bridged by a top-cell stripe on the waveguide layer
    /// (1,0) whose edges pass exactly through wgA.out (10, 2) and wgB.in (15, 2):
    /// the drawn route IS the connection, so the import derives it from the route
    /// network (route-derived, not an abutment).
    /// </summary>
    private static byte[] TwoWaveguideLibraryBridgedByRoute() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 15000, 0)
            .Boundary(1, 0, (10000, 1750), (15000, 1750), (15000, 2250), (10000, 2250), (10000, 1750))
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

    /// <summary>
    /// TOP whose only instance is a zero-length-straight stand-in ("zeroL": a port
    /// label on a point — an empty flattened bbox). The import succeeds, but every
    /// instance is skipped: a successful run with zero placements.
    /// </summary>
    private static byte[] ZeroGeometryOnlyLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("zeroL", 0, 0)
        .EndCell()
        .BeginCell("zeroL")
            .Text(1, 10, "io", 0, 0)
        .EndCell()
        .EndLibrary()
        .ToArray();

    /// <summary>
    /// TOP whose only content is two references to "np", a cell with a zero-width
    /// (degenerate) extent: the draft has zero size, so the service refuses
    /// registration and BOTH instances are skipped with the identical reason —
    /// the fixture for the grouped-skip reporting.
    /// </summary>
    private static byte[] ZeroWidthCellLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("np", 0, 0)
            .SRef("np", 10000, 0)
        .EndCell()
        .BeginCell("np")
            .Boundary(111, 0, (0, 0), (0, 0), (0, 5000), (0, 5000), (0, 0))
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

    [Fact]
    public void TryBuildOptions_MetalLayersEmpty_LeavesMetalOnAuto()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        vm.MetalLayersText.ShouldBe("", "the default is AUTO — foreign foundry files must not " +
            "inherit Lunima's exporter metal layers");
        vm.TryBuildOptions(out var options, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        options.MetalRouteLayers.ShouldBeNull("null = AUTO (own-export sentinel decides)");
        options.PinDetection.ElectricalLayers.ShouldBeNull();
    }

    [Fact]
    public void TryBuildOptions_MetalLayersSet_FeedsRouteMatchingAndPinInference()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        vm.MetalLayersText = "12,0; 41,0";

        vm.TryBuildOptions(out var options, out _).ShouldBeTrue();

        options.MetalRouteLayers.ShouldBe(new[] { (12, 0), (41, 0) });
        options.PinDetection.ElectricalLayers.ShouldBe(new[] { (12, 0), (41, 0) },
            "an explicit mapping drives the electrical pin inference too");
    }

    [Fact]
    public void TryBuildOptions_MetalLayersMalformed_ReturnsError()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        vm.MetalLayersText = "12";

        vm.TryBuildOptions(out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("12");
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
    public void RerouteConnectionsRequested_IsTrueByDefault()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));

        vm.RerouteConnectionsRequested.ShouldBeTrue(
            "detected connections should come back as real Lunima routing by default");
    }

    // ── Connection reconstruction ────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_GappedFreePins_AreNeverGuessedConnected()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryWithGap()));
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.ImportCompleted.ShouldBeTrue();
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.InternalPaths.ShouldBeEmpty(
            "the 80 µm gap is no abutment and carries no drawn route geometry — " +
            "connectivity comes from the GDS structure only, never from pin proximity");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportAsync_RouteDerivedConnection_HonorsRerouteToggle(bool reroute)
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibraryBridgedByRoute()));
        await vm.StartAnalysisAsync();
        vm.RerouteConnectionsRequested = reroute;

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.ImportCompleted.ShouldBeTrue();
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        var path = group.InternalPaths.ShouldHaveSingleItem(
            "the bridge polygon is consumed by route derivation — one pinned connection, no pin-less leftover");
        path.StartPin.ShouldNotBeNull("a route-derived connection keeps its pins");
        path.IsRouteFrozen.ShouldBe(!reroute,
            "re-routing hands the route-derived connection to Lunima's router as a live route; " +
            "otherwise the imported polygon geometry stays as a frozen cached route");

        var reroutedSuffix = string.Format(
            LocalizationService.Instance.Translate("GdsImport.ResultReroutedSuffix"), 1);
        if (reroute)
            vm.ResultSummaryText.ShouldContain(reroutedSuffix,
                customMessage: "the one route-derived connection is reported as re-routed");
        else
            vm.ResultSummaryText.ShouldNotContain(reroutedSuffix,
                customMessage: "frozen mode hands nothing to the router");
    }

    // ── Error-console mirroring ──────────────────────────────────────────────

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

    // ── Grouped warning mirroring ────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_RepeatedPerInstanceSkips_MirrorOneGroupedLineToTheErrorConsole()
    {
        var console = new ErrorConsoleService();
        var (vm, canvas, _) = CreateDialog(WriteGds(ZeroWidthCellLibrary()), console);
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.ImportCompleted.ShouldBeTrue();
        canvas.Components.ShouldBeEmpty("both instances of the zero-size cell are skipped");
        var grouped = vm.Warnings.Where(w => w.Contains("this instance cannot be placed"))
            .ShouldHaveSingleItem("identical per-instance skip reasons collapse into ONE grouped line");
        grouped.ShouldContain("'np#0'", customMessage: "the first example is named");
        grouped.ShouldContain("× 2 instances", customMessage: "the count is appended");

        // The console receives the same single grouped line, not one entry per instance.
        console.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("this instance cannot be placed"))
            .ShouldBe(1);
        console.Entries.ShouldContain(e => e.Level == LogLevel.Warn && e.Message.Contains("× 2 instances"));
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

    // ── Zoom-to-fit after import ─────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SuccessWithPlacements_FiresZoomToFitOnce()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        var calls = new List<(double W, double H)>();
        vm.ZoomToFitAfterImport = (w, h) => calls.Add((w, h));
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        canvas.Components.ShouldNotBeEmpty();
        var call = calls.ShouldHaveSingleItem(
            "a successful import with placements zooms the canvas to the content exactly once");
        call.ShouldBe((900.0, 800.0),
            "the dialog passes the fallback viewport size; the view layer substitutes the real one");
    }

    [Fact]
    public async Task ImportAsync_ZeroPlacements_DoesNotFireZoomToFit()
    {
        var (vm, canvas, _) = CreateDialog(WriteGds(ZeroGeometryOnlyLibrary()));
        var calls = 0;
        vm.ZoomToFitAfterImport = (_, _) => calls++;
        await vm.StartAnalysisAsync();

        await vm.ImportCommand.ExecuteAsync(null);

        vm.ImportCompleted.ShouldBeTrue();
        canvas.Components.ShouldBeEmpty(
            "the only instance is a zero-geometry stand-in — skipped, not placed");
        calls.ShouldBe(0, "nothing was placed, so there is nothing new to zoom to");
    }

    [Fact]
    public async Task ImportAsync_Failure_DoesNotFireZoomToFit()
    {
        var gdsPath = WriteGds(TwoWaveguideLibrary());
        var (vm, _, _) = CreateDialog(gdsPath);
        var calls = 0;
        vm.ZoomToFitAfterImport = (_, _) => calls++;
        await vm.StartAnalysisAsync();
        // The file vanishes between analysis and import — the run fails mid-flow.
        File.Delete(gdsPath);

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeTrue();
        vm.ImportCompleted.ShouldBeFalse();
        calls.ShouldBe(0, "a failed import must not move the viewport");
    }

    [Fact]
    public async Task ImportAsync_Cancelled_DoesNotFireZoomToFit()
    {
        var (vm, _, _) = CreateDialog(WriteGds(TwoWaveguideLibrary()));
        var calls = 0;
        vm.ZoomToFitAfterImport = (_, _) => calls++;
        await vm.StartAnalysisAsync();

        var import = vm.ImportCommand.ExecuteAsync(null);
        // Same determinism argument as ImportAsync_Cancelled_StatusNamesPlacedCountAndRemedy:
        // the cancel lands before any placement.
        vm.CurrentCts.ShouldNotBeNull().Cancel();
        await import;

        vm.ImportCompleted.ShouldBeFalse();
        calls.ShouldBe(0, "a cancelled import must not move the viewport");
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

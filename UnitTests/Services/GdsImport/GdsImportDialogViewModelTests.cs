using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
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

    private string WriteGds(byte[] content, string fileName = "circuit.gds")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    private (GdsImportDialogViewModel vm, DesignCanvasViewModel canvas, LibrarySink sink) CreateDialog(string gdsPath)
    {
        var sink = new LibrarySink(_prefsPath);
        var canvas = new DesignCanvasViewModel();
        var service = new GdsImportService(Store(), () => sink.Templates.ToList(), sink.Register);
        var executor = new GdsPlacementExecutor(canvas, new CommandManager(), () => sink.Templates.ToList());
        return (new GdsImportDialogViewModel(gdsPath, service, executor), canvas, sink);
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
        group.InternalPaths.Count.ShouldBe(2,
            "the flag reached the executor: wgA.out↔wgB.in across the gap plus the wgA.in↔wgB.out wrap-around");
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

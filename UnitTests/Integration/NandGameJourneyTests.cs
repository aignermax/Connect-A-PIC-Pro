using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Import.Gds;
using Moq;
using Shouldly;
using UnitTests.Export;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// End-to-end journey for the NAND game (issue #976, rung 4): a student edits and
/// saves their gate design and expects the logic to survive the round trip and the
/// layout to export. Walks the full user journey headlessly over the shipped
/// <c>examples/Logic Gate AND-from-NAND.lun</c>: load through the real load path →
/// extract the AND table through the Truth Table panel VM → save to a temp .lun via
/// the real save path → reload → re-extract (table and raw powers must be identical)
/// → export the reloaded design to GDS (nazca-gated, structural assertions only).
/// The facts share one fixture so every step builds on the state the previous
/// journey step produced.
/// </summary>
public class NandGameJourneyTests : IClassFixture<NandGameJourneyFixture>
{
    private const double PowerTolerance = 1e-6;
    private const double ExtinguishedPower = 0.0;
    private const double SingleInputPower = 1.0 / 6.0;
    private const double BothInputsPower = 1.0 / 3.0;

    private readonly NandGameJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public NandGameJourneyTests(NandGameJourneyFixture journey) => _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAsSingleAndFromNandGroup()
    {
        var group = _journey.OriginalGroup;

        group.GroupName.ShouldBe(NandGameJourneyFixture.GroupName);
        group.ExternalPins.Select(p => p.Name)
            .ShouldBe(new[] { "A", "B", "BIAS", "BIAS2", "Y" }, ignoreOrder: true);
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "every external pin stays bound to a simulatable component pin");
        group.ChildComponents.Count.ShouldBe(9);
        group.InternalPaths.Count.ShouldBe(8);
    }

    [Fact]
    public void Step2_Extract_ThroughPanelVm_ProducesPinnedAndTable()
    {
        var table = _journey.OriginalTable;

        table.HasResult.ShouldBeTrue("the panel extraction must succeed on the loaded example");
        table.InputHeaders.ShouldBe(new[] { "A", "B" });
        table.OutputHeaders.ShouldBe(new[] { "Y" });
        table.Rows.Count.ShouldBe(4, "two logic inputs produce four rows");
        AssertPanelRow(table, "0 0", expectedBit: false, "0.00");
        AssertPanelRow(table, "1 0", expectedBit: false, "0.17");
        AssertPanelRow(table, "0 1", expectedBit: false, "0.17");
        AssertPanelRow(table, "1 1", expectedBit: true, "0.33");

        // The panel displays two decimals; the pinned physics is exact — assert the
        // raw simulated powers behind the displayed table at full precision.
        AssertRawRow(_journey.OriginalRawTable, 0, expectedBit: false, ExtinguishedPower);
        AssertRawRow(_journey.OriginalRawTable, 1, expectedBit: false, SingleInputPower);
        AssertRawRow(_journey.OriginalRawTable, 2, expectedBit: false, SingleInputPower);
        AssertRawRow(_journey.OriginalRawTable, 3, expectedBit: true, BothInputsPower);
    }

    [Fact]
    public void Step3_SaveReload_GroupSurvivesTheRoundTrip()
    {
        File.Exists(_journey.SavedDesignPath).ShouldBeTrue("the real save path wrote the temp .lun");
        _journey.ReloadedGroup.GroupName.ShouldBe(NandGameJourneyFixture.GroupName);
        _journey.ReloadedGroup.ChildComponents.Select(c => c.Identifier)
            .ShouldBe(_journey.OriginalGroup.ChildComponents.Select(c => c.Identifier), ignoreOrder: true);
        _journey.ReloadedGroup.InternalPaths.Count.ShouldBe(_journey.OriginalGroup.InternalPaths.Count);
        _journey.ReloadedGroup.ExternalPins.Select(p => p.Name)
            .ShouldBe(_journey.OriginalGroup.ExternalPins.Select(p => p.Name), ignoreOrder: true);
        _journey.ReloadedGroup.ExternalPins.ShouldAllBe(p => p.InternalPin != null,
            "external pins must stay bound to their child pins after the round trip");
    }

    [Fact]
    public void Step4_ReExtract_ThroughPanelVm_IsIdenticalToBeforeSave()
    {
        var reloaded = _journey.ReloadedTable;

        reloaded.HasResult.ShouldBeTrue("the panel extraction must succeed on the reloaded design");
        reloaded.BiasSummaryText.ShouldBe(_journey.OriginalTable.BiasSummaryText);
        reloaded.BiasSummaryText.ShouldNotBeNullOrWhiteSpace("BIAS and BIAS2 stay assigned");
        reloaded.Rows.Count.ShouldBe(_journey.OriginalTable.Rows.Count);
        for (var i = 0; i < reloaded.Rows.Count; i++)
        {
            var before = _journey.OriginalTable.Rows[i];
            var after = reloaded.Rows[i];
            after.InputBitsText.ShouldBe(before.InputBitsText);
            after.OutputCells[0].IsOne.ShouldBe(before.OutputCells[0].IsOne,
                $"row {i} ({before.InputBitsText}): the logic bit must survive save → load");
            after.OutputCells[0].PowerText.ShouldBe(before.OutputCells[0].PowerText,
                $"row {i} ({before.InputBitsText}): the displayed power must survive save → load");
        }

        for (var i = 0; i < _journey.OriginalRawTable.Rows.Count; i++)
        {
            var beforePower = _journey.OriginalRawTable.Rows[i].Outputs["Y"].Power;
            _journey.ReloadedRawTable.Rows[i].Outputs["Y"].Power.ShouldBe(beforePower, PowerTolerance,
                $"row {i}: the raw simulated power must survive save → load");
        }
    }

    [SkippableFact]
    public async Task Step5_GdsExport_ReloadedDesign_WritesDesignTopCellWithGeometry()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the GDS export needs the real engine.");

        var script = new SimpleNazcaExporter().Export(_journey.ReloadedCanvas);
        // The design cell is the GDS top cell, not a nazca wrapper.
        script.ShouldContain("nd.export_gds(topcells=[design]");

        var exportDir = Path.Combine(_journey.WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "nand_game.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export failed:\n{run.StdOut}\n{run.StdErr}");

        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);

        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design");
        var designCell = library.Cells["ConnectAPIC_Design"];
        designCell.Elements.OfType<GdsReference>().ShouldNotBeEmpty(
            "the group's child components are placed as cell references");
        designCell.Elements.OfType<GdsPolygon>().ShouldNotBeEmpty(
            "the routed waveguides flatten into real top-cell geometry");
    }

    private static void AssertPanelRow(TruthTableViewModel table, string bits, bool expectedBit, string powerText)
    {
        var cell = table.Rows.Single(r => r.InputBitsText == bits).OutputCells[0];
        cell.IsOne.ShouldBe(expectedBit, $"output bit for input pattern {bits}");
        cell.PowerText.ShouldBe(powerText, $"displayed power for input pattern {bits}");
    }

    private static void AssertRawRow(TruthTable table, int pattern, bool expectedBit, double expectedPower)
    {
        var output = table.Rows[pattern].Outputs["Y"];
        output.IsOne.ShouldBe(expectedBit, $"raw output bit for input pattern {pattern}");
        output.Power.ShouldBe(expectedPower, PowerTolerance, $"raw power for input pattern {pattern}");
    }
}

/// <summary>
/// Shared fixture for <see cref="NandGameJourneyTests"/>: performs the journey's
/// stateful steps once (load → extract → save → reload → re-extract) so each fact
/// asserts one step of the same continuous journey.
/// </summary>
public class NandGameJourneyFixture : IAsyncLifetime
{
    /// <summary>Group name inside the shipped example.</summary>
    public const string GroupName = "AND from NAND Gate";

    private const string ExampleFileName = "Logic Gate AND-from-NAND.lun";
    private const double AndThreshold = 0.25;
    private const int WavelengthNm = 1550;

    private static readonly string[] InputsAb = { "A", "B" };
    private static readonly string[] Biases = { "BIAS", "BIAS2" };
    private static readonly string[] Outputs = { "Y" };

    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    /// <summary>Temp working directory for the saved .lun and the GDS export.</summary>
    public string WorkDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "nand-journey-" + Guid.NewGuid().ToString("N"));

    /// <summary>Path of the temp .lun written by the real save path.</summary>
    public string SavedDesignPath => Path.Combine(WorkDirectory, "nand-game-roundtrip.lun");

    /// <summary>The group as loaded from the shipped example.</summary>
    public ComponentGroup OriginalGroup { get; private set; } = null!;

    /// <summary>The group after save → reload.</summary>
    public ComponentGroup ReloadedGroup { get; private set; } = null!;

    /// <summary>Canvas holding the reloaded design (export input for step 5).</summary>
    public DesignCanvasViewModel ReloadedCanvas { get; private set; } = null!;

    /// <summary>Panel-VM extraction of the original design.</summary>
    public TruthTableViewModel OriginalTable { get; private set; } = null!;

    /// <summary>Panel-VM extraction of the reloaded design.</summary>
    public TruthTableViewModel ReloadedTable { get; private set; } = null!;

    /// <summary>Full-precision table behind <see cref="OriginalTable"/>.</summary>
    public TruthTable OriginalRawTable { get; private set; } = null!;

    /// <summary>Full-precision table behind <see cref="ReloadedTable"/>.</summary>
    public TruthTable ReloadedRawTable { get; private set; } = null!;

    /// <summary>Runs journey steps 1–4: load, extract, save, reload, re-extract.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WorkDirectory);

        var (saveOps, originalCanvas) = CreateFileOperations();
        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        (await saveOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"the shipped example '{ExampleFileName}' must load through the real load path");
        OriginalGroup = SingleGroupOf(originalCanvas);
        OriginalTable = await ExtractViaPanelVm(OriginalGroup, originalCanvas);
        OriginalRawTable = await ExtractRaw(OriginalGroup);

        await SaveToFile(saveOps, SavedDesignPath);

        var (loadOps, reloadedCanvas) = CreateFileOperations();
        (await loadOps.LoadDesignFromPathAsync(SavedDesignPath)).ShouldBeTrue(
            "the saved design must reload through the real load path");
        ReloadedCanvas = reloadedCanvas;
        ReloadedGroup = SingleGroupOf(reloadedCanvas);
        ReloadedTable = await ExtractViaPanelVm(ReloadedGroup, reloadedCanvas);
        ReloadedRawTable = await ExtractRaw(ReloadedGroup);
    }

    /// <summary>Removes the temp working directory.</summary>
    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, recursive: true);
        }
        catch
        {
            // temp cleanup is best effort
        }
        return Task.CompletedTask;
    }

    private (FileOperationsViewModel, DesignCanvasViewModel) CreateFileOperations()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            _library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);
        return (vm, canvas);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
    }

    private static ComponentGroup SingleGroupOf(DesignCanvasViewModel canvas) =>
        canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().Single();

    /// <summary>Extracts the AND table through the Truth Table panel VM, as the user would.</summary>
    private static async Task<TruthTableViewModel> ExtractViaPanelVm(
        ComponentGroup group, DesignCanvasViewModel canvas)
    {
        var component = new ComponentViewModel(group);
        canvas.Selection.SelectSingle(component);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);
        foreach (var name in InputsAb)
            vm.InputPins.Single(p => p.PinName == name).IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "Y").IsChecked = true;
        foreach (var name in Biases)
            vm.BiasPins.Single(p => p.PinName == name).IsChecked = true;
        vm.Threshold = AndThreshold;
        await vm.ExtractCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>Extracts the same table at full precision via the panel's extractor.</summary>
    private static Task<TruthTable> ExtractRaw(ComponentGroup group) =>
        new TruthTableExtractor().ExtractAsync(group, InputsAb, Outputs, Biases, AndThreshold, WavelengthNm);
}

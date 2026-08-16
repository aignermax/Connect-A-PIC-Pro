using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Moq;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// NAND-game truth-table journey (issue #952, rung 4 of the #537 roadmap) — the
/// student loop exercised headlessly through the real <see cref="MainViewModel"/>
/// wiring (<see cref="MainViewModelTestHelper"/>), no mocked seams:
///
///   Step 1: Build the OR-gate circuit from the #946 <see cref="LogicGateFixtureFactory"/>
///           layout (two inputs → 50/50 combiner → one output) as real placed components.
///   Step 2: Group it via the real grouping operation (Ctrl+G equivalent,
///           <see cref="CreateGroupCommand"/>) — the group exposes the expected external pins.
///   Step 3: Select the group through the real OnSelectionChanged path — the Truth
///           Table panel activates and its pin checkbox lists match the external pins.
///   Step 4: Extract via <see cref="TruthTableViewModel"/> (2 inputs + 1 output,
///           threshold 0.25) — the OR table with raw powers 0.00 / 0.50 / 0.50 / 1.00.
///   Step 5: Save to a temp .lun via <see cref="FileOperationsViewModel"/> and reload
///           into a fresh MainViewModel — the group and its pins survive.
///   Step 6: Re-select the reloaded group and re-extract with the same settings —
///           the table is identical to step 4.
///
/// Combiner physics: one active input delivers half its power at the output
/// (0.5 ≥ 0.25 → logic 1); both inputs recombine coherently into full power (1.0).
/// </summary>
public class NandGameTruthTableJourneyTests : IDisposable
{
    private const double Threshold = 0.25;
    private const double PowerTolerance = 1e-6;
    private const double WireGapMicrometers = 5;
    private const double CombinerOut1OffsetY = 10;
    private const double WaveguidePinOffsetY = 2.5;

    private const string CombinerIdentifier = "combine";
    private const string OutputIdentifier = "out";
    private const string GroupName = "OR Gate";
    private const string InputPinA = "combine_in1";
    private const string InputPinB = "combine_in2";
    private const string OutputPinY = "out_b0";

    // Creation order of the group's external pins: the combiner's free ports first
    // (out1 is occupied by the internal route to the output waveguide), then the
    // waveguide's free output port.
    private static readonly string[] ExpectedExternalPins =
        { InputPinA, InputPinB, "combine_out2", OutputPinY };

    private readonly string _designFilePath =
        Path.Combine(Path.GetTempPath(), $"nand_truth_table_{Guid.NewGuid():N}.lun");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_designFilePath)) File.Delete(_designFilePath);
        }
        catch
        {
            // Temp cleanup must never fail the test run.
        }
    }

    [Fact]
    public void Step1_BuildOrGateCircuit_ComponentsPlacedAndConnected()
    {
        var canvas = new DesignCanvasViewModel();
        CreateMainViewModel(canvas);

        BuildOrGateCircuit(canvas);

        canvas.Components.Count.ShouldBe(2,
            "Step 1: the combiner and the output waveguide sit on the canvas");
        canvas.ConnectionManager.Connections.Count.ShouldBe(1,
            "Step 1: the combiner output is routed into the output waveguide");
        var combiner = FindComponent(canvas, CombinerIdentifier);
        combiner.PhysicalPins.Count.ShouldBe(4, "Step 1: the combiner keeps its four ports");
        combiner.PhysicalPins.ShouldAllBe(p => p.LogicalPin != null,
            "Step 1: every combiner port stays simulatable");
    }

    [Fact]
    public void Step2_GroupCircuit_ExposesExpectedExternalPins()
    {
        var canvas = new DesignCanvasViewModel();
        CreateMainViewModel(canvas);
        BuildOrGateCircuit(canvas);

        var group = GroupCircuit(canvas);

        group.ChildComponents.Count.ShouldBe(2, "Step 2: the group owns combiner + waveguide");
        group.InternalPaths.Count.ShouldBe(1,
            "Step 2: the combiner→waveguide route freezes into the group");
        canvas.Components.Count.ShouldBe(1, "Step 2: only the group remains on the canvas");
        group.ExternalPins.Select(p => p.Name).ShouldBe(ExpectedExternalPins,
            "Step 2: the free ports become the group's external pins");
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "Step 2: every external pin stays bound to a simulatable component pin");
    }

    [Fact]
    public void Step3_SelectGroup_TruthTablePanelActivatesWithExternalPins()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = CreateMainViewModel(canvas);
        BuildOrGateCircuit(canvas);
        var group = GroupCircuit(canvas);

        SelectGroup(mainVm, canvas, group);

        var truthTable = mainVm.RightPanel.TruthTable;
        truthTable.IsGroupSelected.ShouldBeTrue(
            "Step 3: selecting exactly one group activates the Truth Table panel");
        truthTable.InputPins.Select(p => p.PinName).ShouldBe(ExpectedExternalPins,
            "Step 3: the input checkboxes list the group's external pins");
        truthTable.OutputPins.Select(p => p.PinName).ShouldBe(ExpectedExternalPins,
            "Step 3: the output checkboxes list the group's external pins");
    }

    [Fact]
    public async Task Step4_Extract_ProducesOrTableWithRawPowers()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = CreateMainViewModel(canvas);
        BuildOrGateCircuit(canvas);
        SelectGroup(mainVm, canvas, GroupCircuit(canvas));

        await ExtractOrTable(mainVm);

        AssertOrTable(mainVm.RightPanel.TruthTable, "Step 4");
    }

    [Fact]
    public async Task Step5_SaveAndReload_GroupSurvivesWithSamePins()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = CreateMainViewModel(canvas);
        BuildOrGateCircuit(canvas);
        SelectGroup(mainVm, canvas, GroupCircuit(canvas));

        var (_, freshCanvas) = await SaveAndReload(mainVm);

        var loadedGroup = freshCanvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>().SingleOrDefault();
        loadedGroup.ShouldNotBeNull("Step 5: the reloaded design still contains the group");
        loadedGroup!.GroupName.ShouldBe(GroupName, "Step 5: the group keeps its name");
        loadedGroup.ChildComponents.Count.ShouldBe(2, "Step 5: the group keeps its children");
        loadedGroup.InternalPaths.Count.ShouldBe(1, "Step 5: the group keeps its frozen route");
        loadedGroup.ExternalPins.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(ExpectedExternalPins.OrderBy(n => n, StringComparer.Ordinal),
                "Step 5: the group keeps its external pins");
        loadedGroup.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "Step 5: every external pin stays simulatable after the round-trip");
    }

    [Fact]
    public async Task Step6_ReExtractAfterReload_TableIsIdentical()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = CreateMainViewModel(canvas);
        BuildOrGateCircuit(canvas);
        SelectGroup(mainVm, canvas, GroupCircuit(canvas));
        await ExtractOrTable(mainVm);
        AssertOrTable(mainVm.RightPanel.TruthTable, "Step 6 (before save)");
        var before = SnapshotTable(mainVm.RightPanel.TruthTable);

        var (freshVm, freshCanvas) = await SaveAndReload(mainVm);
        var loadedGroup = freshCanvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>().Single();
        SelectGroup(freshVm, freshCanvas, loadedGroup);
        freshVm.RightPanel.TruthTable.IsGroupSelected.ShouldBeTrue(
            "Step 6: the reloaded group activates the Truth Table panel");

        await ExtractOrTable(freshVm);

        AssertOrTable(freshVm.RightPanel.TruthTable, "Step 6 (after reload)");
        var after = SnapshotTable(freshVm.RightPanel.TruthTable);
        after.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            before.Keys.OrderBy(k => k, StringComparer.Ordinal),
            "Step 6: the reloaded group produces the same input patterns");
        foreach (var pattern in before.Keys)
        {
            after[pattern].IsOne.ShouldBe(before[pattern].IsOne,
                $"Step 6: the output bit for {pattern} survives the round-trip");
            after[pattern].Power.ShouldBe(before[pattern].Power, PowerTolerance,
                $"Step 6: the raw power for {pattern} survives the round-trip");
        }
    }

    // ── Journey helpers ─────────────────────────────────────────────────────────

    /// <summary>Creates the real MainViewModel wiring with the fixture templates registered.</summary>
    private static MainViewModel CreateMainViewModel(DesignCanvasViewModel canvas)
    {
        var mainVm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        // FileOperations resolves saved components against LeftPanel.AllTemplates —
        // the fixture templates must be registered there for the .lun round-trip.
        mainVm.LeftPanel.AllTemplates.Add(LogicGateFixtureFactory.CreateCombinerTemplate());
        mainVm.LeftPanel.AllTemplates.Add(LogicGateFixtureFactory.CreateWaveguideTemplate());
        return mainVm;
    }

    /// <summary>Step 1: places combiner + output waveguide and routes combiner.out1 → out.a0.</summary>
    private static void BuildOrGateCircuit(DesignCanvasViewModel canvas)
    {
        var combinerTemplate = LogicGateFixtureFactory.CreateCombinerTemplate();
        var waveguideTemplate = LogicGateFixtureFactory.CreateWaveguideTemplate();

        var combiner = ComponentTemplates.CreateFromTemplate(combinerTemplate, 0, 0);
        combiner.Identifier = CombinerIdentifier;
        canvas.AddComponent(combiner, combinerTemplate.Name);

        var output = ComponentTemplates.CreateFromTemplate(waveguideTemplate,
            combinerTemplate.WidthMicrometers + WireGapMicrometers,
            CombinerOut1OffsetY - WaveguidePinOffsetY);
        output.Identifier = OutputIdentifier;
        canvas.AddComponent(output, waveguideTemplate.Name);

        Wire(canvas, Pin(combiner, "out1"), Pin(output, "a0"));
    }

    /// <summary>Step 2: groups the circuit via the real Ctrl+G command.</summary>
    private static ComponentGroup GroupCircuit(DesignCanvasViewModel canvas)
    {
        var command = new CreateGroupCommand(
            canvas,
            canvas.Components.ToList(),
            GroupName);
        command.Execute();
        return command.CreatedGroup.ShouldNotBeNull(
            "Step 2: grouping the two components must create a group");
    }

    /// <summary>Selects the group through the real selection-changed wiring of MainViewModel.</summary>
    private static void SelectGroup(MainViewModel mainVm, DesignCanvasViewModel canvas, ComponentGroup group)
    {
        var groupViewModel = canvas.Components.Single(c => c.Component == group);
        canvas.Selection.SelectSingle(groupViewModel);
        mainVm.CanvasInteraction.SelectedComponent = groupViewModel;
    }

    /// <summary>Step 4/6: checks a + b as inputs and y as output, then extracts at threshold 0.25.</summary>
    private static async Task ExtractOrTable(MainViewModel mainVm)
    {
        var truthTable = mainVm.RightPanel.TruthTable;
        truthTable.InputPins.Single(p => p.PinName == InputPinA).IsChecked = true;
        truthTable.InputPins.Single(p => p.PinName == InputPinB).IsChecked = true;
        truthTable.OutputPins.Single(p => p.PinName == OutputPinY).IsChecked = true;
        truthTable.Threshold = Threshold;
        await truthTable.ExtractCommand.ExecuteAsync(null);
    }

    /// <summary>Step 5: saves via the real file operations and reloads into a fresh MainViewModel.</summary>
    private async Task<(MainViewModel FreshVm, DesignCanvasViewModel FreshCanvas)> SaveAndReload(
        MainViewModel mainVm)
    {
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        mainVm.FileDialogService = saveDialog.Object;
        await mainVm.FileOperations.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("Step 5: the design file must be written");

        var freshCanvas = new DesignCanvasViewModel();
        var freshVm = CreateMainViewModel(freshCanvas);
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        freshVm.FileDialogService = loadDialog.Object;
        await freshVm.FileOperations.LoadDesignCommand.ExecuteAsync(null);
        return (freshVm, freshCanvas);
    }

    /// <summary>Asserts the OR truth table: bits 00→0, 01→1, 10→1, 11→1 with raw powers.</summary>
    private static void AssertOrTable(TruthTableViewModel truthTable, string step)
    {
        truthTable.HasResult.ShouldBeTrue($"{step}: the extraction must produce a result");
        truthTable.InputHeaders.ShouldBe(new[] { InputPinA, InputPinB });
        truthTable.OutputHeaders.ShouldBe(new[] { OutputPinY });
        truthTable.Rows.Count.ShouldBe(4, $"{step}: two logic inputs produce four rows");

        AssertRow(truthTable, "0 0", false, 0.00, step);
        AssertRow(truthTable, "1 0", true, 0.50, step);
        AssertRow(truthTable, "0 1", true, 0.50, step);
        AssertRow(truthTable, "1 1", true, 1.00, step);
    }

    private static void AssertRow(
        TruthTableViewModel truthTable, string bits, bool expectedBit, double expectedPower, string step)
    {
        var row = truthTable.Rows.Single(r => r.InputBitsText == bits);
        row.OutputCells.Count.ShouldBe(1, $"{step}: exactly one output column");
        row.OutputCells[0].IsOne.ShouldBe(expectedBit, $"{step}: OR bit for {bits}");
        ParsePower(row.OutputCells[0]).ShouldBe(expectedPower, PowerTolerance,
            $"{step}: raw power shown in the row cell for {bits}");
    }

    /// <summary>
    /// Captures the table keyed by a canonical bit pattern (bits ordered by pin name),
    /// so the before/after comparison does not depend on external-pin ordering.
    /// </summary>
    private static Dictionary<string, (bool IsOne, double Power)> SnapshotTable(TruthTableViewModel truthTable)
    {
        var inputsByName = truthTable.InputHeaders.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var snapshot = new Dictionary<string, (bool IsOne, double Power)>();
        foreach (var row in truthTable.Rows)
        {
            var bits = row.InputBitsText.Split(' ');
            var canonical = string.Join(" ",
                inputsByName.Select(name => bits[truthTable.InputHeaders.IndexOf(name)]));
            snapshot[canonical] = (row.OutputCells[0].IsOne, ParsePower(row.OutputCells[0]));
        }
        return snapshot;
    }

    private static double ParsePower(TruthTableOutputCellViewModel cell) =>
        double.Parse(cell.PowerText, CultureInfo.InvariantCulture);

    private static Component FindComponent(DesignCanvasViewModel canvas, string identifier) =>
        canvas.Components.Single(c => c.Component.Identifier == identifier).Component;

    private static PhysicalPin Pin(Component component, string pinName) =>
        component.PhysicalPins.Single(p => p.Name == pinName);

    /// <summary>Connects two pins with an explicit straight route, frozen for determinism.</summary>
    private static void Wire(DesignCanvasViewModel canvas, PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        var connection = canvas.ConnectPinsWithCachedRoute(from, to, path);
        connection.ShouldNotBeNull($"route {from.Name} -> {to.Name} must be created");
        connection!.Connection.IsRouteFrozen = true;
    }
}

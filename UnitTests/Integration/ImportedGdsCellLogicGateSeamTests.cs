using System.Globalization;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;
using Journey = UnitTests.Integration.ImportedGdsCellLogicGateSeamJourney;

namespace UnitTests.Integration;

/// <summary>
/// Rung-1 × rung-4 seam journey (issue #1087): an imported GDS black-box cell —
/// carrying the default lossless pass-through S-matrix of #1005/#1012 — spliced
/// into the signal path of a logic gate group. The whole rung-4 chain must keep
/// working with the imported cell inside the group: truth-table extraction over
/// the real S-matrix simulation (#934) yields the table of the SAME group without
/// the cell (the pass-through is optically neutral), pin roles and threshold
/// persist into the .lun (#984), and after save → load the group still owns the
/// imported cell, re-extracts the identical table, and re-assembles through
/// <see cref="LogicNetworkAssembler"/> (#988) with non-zero output levels (no
/// black hole). Every step asserts with an explicit message naming its seam.
/// </summary>
public class ImportedGdsCellLogicGateSeamTests : IDisposable
{
    private const double PowerTolerance = 1e-6;

    private static readonly string[] ExpectedExternalPins =
        { Journey.InputPinA, Journey.InputPinB, "combine_out2", Journey.OutputPinY };

    private readonly Journey _journey = new();

    public void Dispose() => _journey.Dispose();

    [Fact]
    public async Task Step1_ImportCell_TemplateRegisteredWithLosslessPassThrough()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = Journey.CreateMainViewModel(canvas);

        var template = await _journey.ImportCellAsync(mainVm);

        template.Name.ShouldBe(Journey.ImportedCellName,
            "Step 1: the GDS cell registers as a library template");
        template.PinDefinitions.Select(p => p.Name).ShouldBe(new[] { "in", "out" }, ignoreOrder: true,
            "Step 1: the imported black box exposes exactly its two optical pins");
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        var transfers = component.WaveLengthToSMatrixMap[Journey.WavelengthNm].GetNonNullValues();
        transfers.Count.ShouldBe(2,
            "Step 1: two optical pins default to a bidirectional pass-through (#1005/#1012)");
        transfers.Values.ShouldAllBe(v => Math.Abs(v.Magnitude - 1.0) < PowerTolerance,
            "Step 1: the default pass-through is lossless (|S| = 1 in both directions)");
    }

    [Fact]
    public async Task Step2_GroupWithSplicedCell_ExposesSameExternalPinsAsUnspliced()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = Journey.CreateMainViewModel(canvas);
        var template = await _journey.ImportCellAsync(mainVm);
        Journey.BuildOrGateCircuit(canvas, template, splice: true);

        var group = Journey.GroupCircuit(canvas);

        group.ChildComponents.Count.ShouldBe(3,
            "Step 2: the group owns combiner + imported cell + output waveguide");
        group.InternalPaths.Count.ShouldBe(2,
            "Step 2: both splice routes freeze into the group");
        SortedNames(group.ExternalPins.Select(p => p.Name)).ShouldBe(SortedNames(ExpectedExternalPins),
            "Step 2: the consumed imported pins surface nothing — the gate interface is unchanged");
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "Step 2: every external pin stays bound to a simulatable component pin");
    }

    [Fact]
    public async Task Step3_Extract_IdenticalToSameGroupWithoutImportedCell()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = Journey.CreateMainViewModel(canvas);
        var template = await _journey.ImportCellAsync(mainVm);
        Journey.BuildOrGateCircuit(canvas, template, splice: true);
        var group = Journey.GroupCircuit(canvas);
        Journey.SelectGroup(mainVm, canvas, group);

        await Journey.ExtractOrTable(mainVm);

        AssertOrTable(mainVm.RightPanel.TruthTable, "Step 3");
        var spliced = await ExtractRawTable(group);
        var reference = await ExtractReferenceTableWithoutImportedCell();
        spliced.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(reference.Keys.OrderBy(k => k, StringComparer.Ordinal),
                "Step 3: the spliced group sees the same input patterns");
        foreach (var pattern in reference.Keys)
        {
            spliced[pattern].IsOne.ShouldBe(reference[pattern].IsOne,
                $"Step 3: the imported cell must not flip the output bit for {pattern}");
            spliced[pattern].Power.ShouldBe(reference[pattern].Power, PowerTolerance,
                $"Step 3: the pass-through must be optically neutral for {pattern} " +
                "(raw simulated power — the panel's F2 text would hide a small drift)");
        }
    }

    [Fact]
    public async Task Step4_Save_PersistsPinRolesThresholdAndEmbeddedGdsSet()
    {
        var (_, _, path) = await BuildExtractAndSave();

        var fileText = File.ReadAllText(path);
        fileText.ShouldContain("TruthTablePinAssignment", Case.Sensitive,
            "Step 4: the extracted pin roles persist into the .lun (#984)");
        fileText.ShouldContain("\"Threshold\": 0.25", Case.Sensitive,
            "Step 4: the extraction threshold persists into the .lun (#984)");
        fileText.ShouldContain("ImportedGdsComponents", Case.Sensitive,
            "Step 4: the imported GDS set embeds into the .lun for the design-scope restore");
    }

    [Fact]
    public async Task Step5_Reload_ImportedCellStillPartOfGroupWithRoles()
    {
        var (_, _, path) = await BuildExtractAndSave();

        var (_, freshCanvas) = await Journey.Reload(path);

        var loadedGroup = freshCanvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>().SingleOrDefault();
        loadedGroup.ShouldNotBeNull("Step 5: the reloaded design still contains the gate group");
        loadedGroup!.ChildComponents.Count.ShouldBe(3, "Step 5: all three children survive");
        var importedChild = loadedGroup.ChildComponents.SingleOrDefault(
            c => c.Identifier == Journey.ImportedIdentifier);
        importedChild.ShouldNotBeNull("Step 5: the imported cell is still part of the group");
        importedChild!.PhysicalPins.ShouldAllBe(p => p.LogicalPin != null,
            "Step 5: the reloaded imported cell stays simulatable");
        SortedNames(loadedGroup.ExternalPins.Select(p => p.Name)).ShouldBe(SortedNames(ExpectedExternalPins),
            "Step 5: the gate interface survives the round-trip");
        var assignment = loadedGroup.TruthTablePinAssignment.ShouldNotBeNull(
            "Step 5: the persisted pin roles ride back onto the group (#984)");
        assignment.InputPinNames.ShouldBe(new[] { Journey.InputPinA, Journey.InputPinB });
        assignment.OutputPinNames.ShouldBe(new[] { Journey.OutputPinY });
        assignment.Threshold.ShouldBe(Journey.Threshold);
    }

    [Fact]
    public async Task Step6_ReExtractAndAssemble_IdenticalTableNoBlackHole()
    {
        var (_, savedGroup, path) = await BuildExtractAndSave();
        var before = await ExtractRawTable(savedGroup);

        var (freshVm, freshCanvas) = await Journey.Reload(path);
        var loadedGroup = freshCanvas.Components
            .Select(c => c.Component).OfType<ComponentGroup>().Single();
        Journey.SelectGroup(freshVm, freshCanvas, loadedGroup);
        await Journey.ExtractOrTable(freshVm);

        AssertOrTable(freshVm.RightPanel.TruthTable, "Step 6 (after reload)");
        var after = await ExtractRawTable(loadedGroup);
        after.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(before.Keys.OrderBy(k => k, StringComparer.Ordinal),
                "Step 6: the reloaded group produces the same input patterns");
        foreach (var pattern in before.Keys)
        {
            after[pattern].IsOne.ShouldBe(before[pattern].IsOne,
                $"Step 6: the output bit for {pattern} survives the round-trip");
            after[pattern].Power.ShouldBe(before[pattern].Power, PowerTolerance,
                $"Step 6: the raw power for {pattern} survives the round-trip — no black hole");
        }

        var network = await new LogicNetworkAssembler().AssembleAsync(
            new Component[] { loadedGroup }, Array.Empty<WaveguideConnection>(), Journey.WavelengthNm);
        SortedNames(network.InputPinNames).ShouldBe(SortedNames(
            new[] { $"{Journey.GroupName}.{Journey.InputPinA}", $"{Journey.GroupName}.{Journey.InputPinB}" }),
            "Step 6: the assembler re-extracts the gate from the reloaded design (#988)");
        foreach (var a in new[] { false, true })
        foreach (var b in new[] { false, true })
        {
            var outputs = network.Evaluate(new Dictionary<string, bool>
            {
                [$"{Journey.GroupName}.{Journey.InputPinA}"] = a,
                [$"{Journey.GroupName}.{Journey.InputPinB}"] = b,
            });
            outputs[$"{Journey.GroupName}.{Journey.OutputPinY}"].ShouldBe(a || b,
                $"Step 6: the assembled network reads the OR table for A={a}, B={b}");
        }
    }

    // ── Shared step plumbing ────────────────────────────────────────────────────

    /// <summary>Steps 4–6 share the build → group → extract → save prefix of the journey.</summary>
    private async Task<(MainViewModel MainVm, ComponentGroup Group, string Path)> BuildExtractAndSave()
    {
        var canvas = new DesignCanvasViewModel();
        var mainVm = Journey.CreateMainViewModel(canvas);
        var template = await _journey.ImportCellAsync(mainVm);
        Journey.BuildOrGateCircuit(canvas, template, splice: true);
        var group = Journey.GroupCircuit(canvas);
        Journey.SelectGroup(mainVm, canvas, group);
        await Journey.ExtractOrTable(mainVm);
        return (mainVm, group, await _journey.Save(mainVm));
    }

    /// <summary>The reference table of the SAME group without the imported cell (neutrality baseline).</summary>
    private static async Task<Dictionary<string, (bool IsOne, double Power)>>
        ExtractReferenceTableWithoutImportedCell()
    {
        var canvas = new DesignCanvasViewModel();
        Journey.BuildOrGateCircuit(canvas, null, splice: false);
        return await ExtractRawTable(Journey.GroupCircuit(canvas));
    }

    /// <summary>
    /// Extracts the group's table straight from the <see cref="TruthTableExtractor"/> — raw
    /// simulated powers, bypassing the panel's F2 rounding that would hide a small drift.
    /// </summary>
    private static async Task<Dictionary<string, (bool IsOne, double Power)>> ExtractRawTable(
        ComponentGroup group)
    {
        var inputs = new[] { Journey.InputPinA, Journey.InputPinB };
        var table = await new TruthTableExtractor().ExtractAsync(
            group, inputs, new[] { Journey.OutputPinY }, Journey.Threshold, Journey.WavelengthNm);
        return table.Rows.ToDictionary(
            row => string.Join(" ", inputs.Select(name => row.InputBits[name] ? "1" : "0")),
            row => (row.Outputs[Journey.OutputPinY].IsOne, row.Outputs[Journey.OutputPinY].Power));
    }

    /// <summary>Asserts the OR truth table: bits 00→0, 01→1, 10→1, 11→1 with raw powers.</summary>
    private static void AssertOrTable(TruthTableViewModel truthTable, string step)
    {
        truthTable.HasResult.ShouldBeTrue($"{step}: the extraction must produce a result");
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
        row.OutputCells[0].IsOne.ShouldBe(expectedBit, $"{step}: OR bit for {bits}");
        double.Parse(row.OutputCells[0].PowerText, CultureInfo.InvariantCulture)
            .ShouldBe(expectedPower, PowerTolerance,
                $"{step}: raw power for {bits} — a black-hole cell would read 0.00");
    }

    private static IEnumerable<string> SortedNames(IEnumerable<string> names) =>
        names.OrderBy(n => n, StringComparer.Ordinal);
}

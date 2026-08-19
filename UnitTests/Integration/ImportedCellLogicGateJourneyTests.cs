using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// End-to-end seam journey between rung 1 (GDS import) and rung 4 (logic gates)
/// (issue #1087): an imported black-box cell — its default lossless pass-through
/// S-matrix from #1005/#1012 — spliced into the signal path of a gate group must
/// survive the whole rung-4 chain: truth-table extraction over the real S-matrix
/// simulation (#934), persisted pin roles (#984), .lun save/load, and re-assembly
/// via <see cref="LogicNetworkAssembler"/> (#988). Each step is a separate test
/// over the shared <see cref="ImportedCellLogicGateJourneyFixture"/> so a failure
/// names the broken step.
/// </summary>
public class ImportedCellLogicGateJourneyTests : IClassFixture<ImportedCellLogicGateJourneyFixture>, IDisposable
{
    private const double PowerTolerance = 1e-9;
    private const double PositivePower = 0;

    private readonly ImportedCellLogicGateJourneyFixture _f;
    private readonly List<IDisposable> _loadHosts = new();

    public ImportedCellLogicGateJourneyTests(ImportedCellLogicGateJourneyFixture fixture) => _f = fixture;

    /// <summary>
    /// Step 1 — Import: the 2-optical-pin GDS cell arrives as a black-box
    /// template with its detected in/out pins (the same fixture cell as #1001/#1006).
    /// </summary>
    [Fact]
    public void Step1_Import_CellArrivesWithDetectedPins()
    {
        _f.Outcome.Warnings.ShouldBeEmpty();
        _f.Outcome.RegisteredComponents.Count.ShouldBe(1);
        _f.Outcome.RegisteredComponents[0].CellDraftName.ShouldBe("wg");

        var template = _f.Host.Templates.Single(t =>
            t.Name == "wg" && t.PdkSource == _f.Outcome.UserPdkName);
        template.PinDefinitions.Select(p => p.Name).ShouldBe(
            new[] { "in", "out" }, ignoreOrder: true);
    }

    /// <summary>
    /// Step 2 — Place inside a gate group: the imported cell is the sole child of
    /// the gate group on the canvas, and the group carries its persisted pin roles.
    /// </summary>
    [Fact]
    public void Step2_Place_ImportedCellIsChildOfGateGroup_WithPersistedRoles()
    {
        _f.Canvas.Components.Count.ShouldBe(1,
            "the gate group is the only top-level component on the canvas");
        var group = (ComponentGroup)_f.Canvas.Components[0].Component;
        group.GroupName.ShouldBe(ImportedCellLogicGateJourneyFixture.GateName);

        group.ChildComponents.Count.ShouldBe(1,
            "the imported GDS cell is the group's only child — it is the group's whole signal path");
        var child = group.ChildComponents[0];
        child.PhysicalPins.Select(p => p.Name).ShouldBe(new[] { "in", "out" }, ignoreOrder: true);

        group.TruthTablePinAssignment.ShouldNotBeNull(
            "the pin roles must be persisted before the design is saved");
        group.TruthTablePinAssignment!.InputPinNames.ShouldBe(new[] { "in" });
        group.TruthTablePinAssignment.OutputPinNames.ShouldBe(new[] { "out" });
    }

    /// <summary>
    /// Step 3 — Truth-table extraction (#934): the spliced group's table must be
    /// identical to the same group without the imported cell — the pass-through
    /// must be optically neutral. The active row must also carry real power,
    /// not a black hole.
    /// </summary>
    [Fact]
    public void Step3_TruthTable_IdenticalToGroupWithoutImportedCell()
    {
        _f.SplicedTable.Rows.Count.ShouldBe(2,
            "a one-input buffer yields two truth-table rows");
        _f.BaselineTable.Rows.Count.ShouldBe(2);

        foreach (var (spliced, baseline) in _f.SplicedTable.Rows.Zip(_f.BaselineTable.Rows))
        {
            var input = spliced.InputBits["in"];
            spliced.Outputs["out"].IsOne.ShouldBe(baseline.Outputs["out"].IsOne,
                $"input in={input}: the imported cell's pass-through must be optically neutral — same logic bit");
            spliced.Outputs["out"].Power.ShouldBe(baseline.Outputs["out"].Power, PowerTolerance,
                $"input in={input}: the lossless pass-through must deliver the same power as the plain waveguide");
        }

        var onRow = _f.SplicedTable.Rows.Single(r => r.InputBits["in"]);
        onRow.Outputs["out"].Power.ShouldBeGreaterThan(PositivePower,
            "no black-hole behaviour: the imported cell passes full power to the gate output");
    }

    /// <summary>
    /// Step 4 — Persist roles + save/load (#984): after the .lun round trip the
    /// gate group survives with its persisted roles, and the imported cell is
    /// still part of the group.
    /// </summary>
    [Fact]
    public async Task Step4_SaveLoad_ImportedCellStaysInGroup_WithPersistedRoles()
    {
        var savedPath = await _f.SaveDesign();
        var reloadedCanvas = await ReloadCanvas(savedPath);

        reloadedCanvas.Components.Count.ShouldBe(1,
            "the gate group is the only top-level component after the round trip");
        var loadedGroup = reloadedCanvas.Components.Select(c => c.Component)
            .OfType<ComponentGroup>()
            .Single();
        loadedGroup.GroupName.ShouldBe(ImportedCellLogicGateJourneyFixture.GateName);

        loadedGroup.TruthTablePinAssignment.ShouldNotBeNull(
            "the persisted pin roles must survive the save → load round trip (#984)");
        loadedGroup.TruthTablePinAssignment!.InputPinNames.ShouldBe(new[] { "in" });
        loadedGroup.TruthTablePinAssignment.OutputPinNames.ShouldBe(new[] { "out" });
        loadedGroup.TruthTablePinAssignment.Threshold
            .ShouldBe(ImportedCellLogicGateJourneyFixture.Threshold);

        loadedGroup.ChildComponents.Count.ShouldBe(1,
            "the imported cell is still part of the group after reload");
        var child = loadedGroup.ChildComponents[0];
        child.PhysicalPins.Select(p => p.Name).ShouldBe(new[] { "in", "out" }, ignoreOrder: true);
        child.WidthMicrometers.ShouldBe(10, PowerTolerance);
        child.HeightMicrometers.ShouldBe(4, PowerTolerance);
    }

    /// <summary>
    /// Step 5 — Re-assemble via <see cref="LogicNetworkAssembler"/> (#988): the
    /// reloaded design evaluates to an identical truth table, and the reloaded
    /// group still passes real power — no black-hole behaviour after reload.
    /// </summary>
    [Fact]
    public async Task Step5_Reassemble_IdenticalTruthTable_NoBlackHole()
    {
        var savedPath = await _f.SaveDesign();
        var reloadedCanvas = await ReloadCanvas(savedPath);
        var reassembled = await ImportedCellLogicGateJourneyFixture.Assemble(reloadedCanvas);

        reassembled.InputPinNames.ShouldBe(_f.Network.InputPinNames,
            "the re-assembled network exposes the same inputs");
        reassembled.OutputPinNames.ShouldBe(_f.Network.OutputPinNames,
            "the re-assembled network exposes the same taps");
        foreach (var bit in new[] { false, true })
        {
            reassembled.Evaluate(new Dictionary<string, bool> { [$"{ImportedCellLogicGateJourneyFixture.GateName}.in"] = bit })
                .ShouldBe(_f.Network.Evaluate(new Dictionary<string, bool> { [$"{ImportedCellLogicGateJourneyFixture.GateName}.in"] = bit }),
                    $"identical truth table for in={bit} after save/load and re-assembly");
        }

        var loadedGroup = reloadedCanvas.Components.Select(c => c.Component)
            .OfType<ComponentGroup>()
            .Single();
        var roles = loadedGroup.TruthTablePinAssignment!;
        var loadedTable = await new TruthTableExtractor().ExtractAsync(
            loadedGroup, roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames,
            roles.Threshold, ImportedCellLogicGateJourneyFixture.WavelengthNm);

        loadedTable.Rows.Select(r => (r.InputBits["in"], r.Outputs["out"].IsOne))
            .ShouldBe(_f.SplicedTable.Rows.Select(r => (r.InputBits["in"], r.Outputs["out"].IsOne)),
                "the reloaded group re-extracts to the same truth table");
        var onRow = loadedTable.Rows.Single(r => r.InputBits["in"]);
        onRow.Outputs["out"].Power.ShouldBeGreaterThan(PositivePower,
            "no black-hole behaviour after reload: the imported cell still passes full power");
    }

    /// <summary>Loads a saved .lun into a fresh canvas through the real load path.</summary>
    private async Task<DesignCanvasViewModel> ReloadCanvas(string path)
    {
        var loadHost = new GdsDesignScopeTestHost();
        _loadHosts.Add(loadHost);
        var loadCanvas = new DesignCanvasViewModel();
        await GdsImportJourneyFixture.LoadFromFile(
            GdsImportJourneyFixture.CreateFileOperations(loadCanvas, loadHost), path);
        return loadCanvas;
    }

    /// <summary>Releases every load host created during the tests.</summary>
    public void Dispose()
    {
        foreach (var host in _loadHosts)
            host.Dispose();
    }
}

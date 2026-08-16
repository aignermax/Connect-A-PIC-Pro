using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Headless load test for the shipped logic-gate example
/// <c>examples/Logic Gate AND-from-NAND.lun</c> (issue #971, rung 4 of the NAND game):
/// the file loads through the real load path as a single "AND from NAND Gate" group —
/// the NAND stage of <c>Logic Gate NOT-NAND.lun</c> feeding a second inverter stage
/// with its own bias (AND = NOT(NAND(A,B))). Cascaded passive linear optics has no
/// level restoration, so the inverter bias is weighted to the weakened NAND levels:
/// BIAS2 (315° phase, coupling ratio 33.3%) arrives anti-phase to the resting NAND
/// output and cancels it exactly (raw power 0 at (0,0)), while the single-input rows
/// rest at 1/6 and (1,1) lets BIAS2 pass at 1/3. At threshold 0.25 the table reads
/// AND: 0,0,0,1 — with the darkest 1 exactly 2× the brightest 0.
/// </summary>
public class LogicGateAndFromNandExampleTests
{
    private const string ExampleFileName = "Logic Gate AND-from-NAND.lun";
    private const string GroupName = "AND from NAND Gate";
    private const double PowerTolerance = 1e-6;
    private const double AndThreshold = 0.25;
    private const double ExtinguishedPower = 0.0;
    private const double SingleInputPower = 1.0 / 6.0;
    private const double BothInputsPower = 1.0 / 3.0;
    private const int WavelengthNm = 1550;

    private static readonly string[] InputsAb = { "A", "B" };
    private static readonly string[] Biases = { "BIAS", "BIAS2" };
    private static readonly string[] Outputs = { "Y" };

    [Fact]
    public async Task Example_LoadsAsSingleGroup_WithCleanExternalPins()
    {
        var group = await LoadGateGroup();

        group.GroupName.ShouldBe(GroupName);
        // The group description carries the threshold, the second bias weighting,
        // and the cascade lesson.
        group.Description.ShouldContain("0.25");
        group.Description.ShouldContain("315");
        group.Description.ShouldContain("33.3");
        group.ChildComponents.Select(c => c.Identifier)
            .ShouldBe(new[]
            {
                "input_a", "input_b", "combine", "phase_nand", "recombine",
                "link", "phase_inv", "invert", "output"
            }, ignoreOrder: true);
        group.InternalPaths.Count.ShouldBe(8,
            "both inputs, both stage links, both phase results, and the output are routed inside the group");
        group.ExternalPins.Select(p => p.Name)
            .ShouldBe(new[] { "A", "B", "BIAS", "BIAS2", "Y" }, ignoreOrder: true);
        group.ExternalPins.ShouldAllBe(p => p.InternalPin != null && p.InternalPin.LogicalPin != null,
            "every external pin stays bound to a simulatable component pin");
    }

    [Fact]
    public async Task TruthTable_InputsAbAtThreshold025_ProducesAndBitsWithRawPowers()
    {
        var table = await Extract(InputsAb, Biases, AndThreshold);

        table.BiasPinNames.ShouldBe(Biases);
        table.Rows.Count.ShouldBe(4, "two logic inputs produce four rows");
        AssertRow(table, 0, expectedBit: false, expectedPower: ExtinguishedPower);
        AssertRow(table, 1, expectedBit: false, expectedPower: SingleInputPower);
        AssertRow(table, 2, expectedBit: false, expectedPower: SingleInputPower);
        AssertRow(table, 3, expectedBit: true, expectedPower: BothInputsPower);
    }

    [Fact]
    public async Task TruthTable_CascadeMargin_DarkestOneIsTwiceTheBrightestZero()
    {
        var table = await Extract(InputsAb, Biases, AndThreshold);

        var brightestZero = table.Rows.Take(3).Max(r => r.Outputs["Y"].Power);
        var darkestOne = table.Rows[3].Outputs["Y"].Power;
        (darkestOne / brightestZero).ShouldBeGreaterThanOrEqualTo(2.0,
            "the cascade's honesty bound: without level restoration the margin tops out at exactly 2×");
    }

    [Fact]
    public async Task TruthTable_WithoutSecondBias_PassesLevelInsteadOfInverting()
    {
        var table = await Extract(InputsAb, new[] { "BIAS" }, AndThreshold);

        table.BiasPinNames.ShouldBe(new[] { "BIAS" });
        // Without BIAS2 the second stage is a transparent coupler: the weakened NAND
        // levels just pass through (× 2/3), so the cascade reads NOR, not AND — the
        // second bias is what turns the passing level into an inversion.
        AssertRow(table, 0, expectedBit: true, expectedPower: BothInputsPower);
        AssertRow(table, 1, expectedBit: false, expectedPower: SingleInputPower);
        AssertRow(table, 2, expectedBit: false, expectedPower: SingleInputPower);
        AssertRow(table, 3, expectedBit: false, expectedPower: ExtinguishedPower);
    }

    /// <summary>Loads the shipped example through the real load path and returns its group.</summary>
    private static async Task<ComponentGroup> LoadGateGroup()
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        var canvas = new DesignCanvasViewModel();
        var fileOps = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);

        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        (await fileOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"the shipped example '{ExampleFileName}' must load through the real load path");

        return canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().Single();
    }

    /// <summary>Extracts the gate's truth table at the given analog→digital threshold.</summary>
    private static async Task<TruthTable> Extract(string[] inputs, string[] biases, double threshold)
    {
        var group = await LoadGateGroup();
        var table = await new TruthTableExtractor().ExtractAsync(
            group, inputs, Outputs, biases, threshold, WavelengthNm);
        table.GroupName.ShouldBe(GroupName);
        table.InputPinNames.ShouldBe(inputs);
        table.OutputPinNames.ShouldBe(Outputs);
        return table;
    }

    /// <summary>Asserts one row's output bit and its raw simulated power behind it.</summary>
    private static void AssertRow(TruthTable table, int pattern, bool expectedBit, double expectedPower)
    {
        var row = table.Rows[pattern];
        var output = row.Outputs["Y"];
        output.IsOne.ShouldBe(expectedBit,
            $"threshold {table.PowerThreshold}: output bit for input pattern {pattern}");
        output.Power.ShouldBe(expectedPower, PowerTolerance,
            $"threshold {table.PowerThreshold}: raw power for input pattern {pattern}");
    }
}

using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Gate-level truth-table extraction over the numeric #929 fixtures: a 50/50
/// combiner behaves as an OR gate at threshold 0.25, and a balanced MZI keeps its
/// dark port dark — with the raw powers staying visible next to every bit.
/// </summary>
public class TruthTableExtractorGateTests
{
    private const double Threshold025 = 0.25;

    // |amplitude|² doubles the relative error of the #929 amplitude tolerance
    // (1e-3 around solver convergence noise of ~3e-5), so power asserts use 1e-3.
    private const double PowerTolerance = 1e-3;

    [Fact]
    public async Task ExtractAsync_CombinerAtThreshold025_IsAnOrGateWithVisiblePowers()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "a", "b" },
            new[] { "y" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(4, "two inputs produce exactly four combinations");

        var offOff = RowFor(table, false, false).Outputs["y"];
        offOff.IsOne.ShouldBeFalse();
        offOff.Power.ShouldBe(0.0, PowerTolerance);

        var onOff = RowFor(table, true, false).Outputs["y"];
        onOff.IsOne.ShouldBeTrue("0.5 ≥ 0.25");
        onOff.Power.ShouldBe(0.5, PowerTolerance, "one input splits its power across both coupler outputs");

        var offOn = RowFor(table, false, true).Outputs["y"];
        offOn.IsOne.ShouldBeTrue("0.5 ≥ 0.25");
        offOn.Power.ShouldBe(0.5, PowerTolerance);

        var onOn = RowFor(table, true, true).Outputs["y"];
        onOn.IsOne.ShouldBeTrue();
        onOn.Power.ShouldBe(1.0, PowerTolerance, "both coherent inputs recombine into full power at y");
    }

    [Fact]
    public async Task ExtractAsync_CombinerAtThreshold075_OnlyTheCoherentRowStaysOn()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "a", "b" },
            new[] { "y" },
            powerThreshold: 0.75,
            LogicGateFixtureFactory.WavelengthNm);

        RowFor(table, false, false).Outputs["y"].IsOne.ShouldBeFalse();
        RowFor(table, true, false).Outputs["y"].IsOne.ShouldBeFalse("0.5 < 0.75");
        RowFor(table, false, true).Outputs["y"].IsOne.ShouldBeFalse("0.5 < 0.75");
        RowFor(table, true, true).Outputs["y"].IsOne.ShouldBeTrue("only full recombined power reaches 0.75");
    }

    [Fact]
    public async Task ExtractAsync_BalancedMzi_KeepsDarkPortDarkForEveryCombination()
    {
        var group = LogicGateFixtureFactory.CreateBalancedMziGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "in" },
            new[] { "dark", "bright" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(2);
        foreach (var row in table.Rows)
        {
            row.Outputs["dark"].IsOne.ShouldBeFalse("the dark port of a balanced MZI never reaches threshold");
            row.Outputs["dark"].Power.ShouldBeLessThan(PowerTolerance);
        }

        var inputOn = RowFor(table, true);
        inputOn.Outputs["bright"].IsOne.ShouldBeTrue();
        inputOn.Outputs["bright"].Power.ShouldBe(1.0, PowerTolerance);

        var inputOff = RowFor(table, false);
        inputOff.Outputs["bright"].IsOne.ShouldBeFalse();
        inputOff.Outputs["bright"].Power.ShouldBe(0.0, PowerTolerance);
    }

    [Fact]
    public async Task ExtractAsync_FourInputs_ExtractsAllSixteenRowsBitAccurately()
    {
        var group = LogicGateFixtureFactory.CreateFourBitBusGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "in0", "in1", "in2", "in3" },
            new[] { "out0", "out1", "out2", "out3" },
            powerThreshold: 0.5,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(1 << TruthTableExtractor.MaxLogicInputs);
        foreach (var row in table.Rows)
        {
            for (var i = 0; i < TruthTableExtractor.MaxLogicInputs; i++)
            {
                var expected = row.InputBits[$"in{i}"];
                var output = row.Outputs[$"out{i}"];
                output.IsOne.ShouldBe(expected, $"lane {i} is a straight-through waveguide");
                output.Power.ShouldBe(expected ? 1.0 : 0.0, PowerTolerance);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_TwoRunsOnIdenticalGroups_ProduceIdenticalPowers()
    {
        var first = await new TruthTableExtractor().ExtractAsync(
            LogicGateFixtureFactory.CreateCombinerGroup(),
            new[] { "a" },
            new[] { "y" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);
        var second = await new TruthTableExtractor().ExtractAsync(
            LogicGateFixtureFactory.CreateCombinerGroup(),
            new[] { "a" },
            new[] { "y" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        first.Rows.Select(r => r.Outputs["y"].Power)
            .ShouldBe(second.Rows.Select(r => r.Outputs["y"].Power));
    }

    [Fact]
    public async Task ExtractAsync_ResultCarriesExtractionContext()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "a", "b" },
            new[] { "y" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        table.GroupName.ShouldBe(group.GroupName);
        table.InputPinNames.ShouldBe(new[] { "a", "b" });
        table.OutputPinNames.ShouldBe(new[] { "y" });
        table.PowerThreshold.ShouldBe(Threshold025);
        table.WavelengthNm.ShouldBe(LogicGateFixtureFactory.WavelengthNm);
    }

    /// <summary>Finds the row whose input bits match the given levels (order of InputPinNames).</summary>
    private static TruthTableRow RowFor(TruthTable table, params bool[] inputLevels)
    {
        table.InputPinNames.Count.ShouldBe(inputLevels.Length);
        return table.Rows.Single(row => table.InputPinNames
            .Select((name, i) => row.InputBits[name] == inputLevels[i])
            .All(matches => matches));
    }
}

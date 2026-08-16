using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Bias-input capability of <see cref="TruthTableExtractor"/> (issue #964, rung 4):
/// bias pins are held constantly on — a coherent reference with unit amplitude and
/// zero phase — so interference-based gates (NOT per MZI) become extractable. They
/// never appear as an InputBits column and never count toward the input limit.
/// </summary>
public class TruthTableExtractorBiasTests
{
    private const double Threshold025 = 0.25;

    // Same solver-noise budget as TruthTableExtractorGateTests (#929 fixtures).
    private const double PowerTolerance = 1e-3;

    [Fact]
    public async Task ExtractAsync_NotMziWithBias_InvertsAtTheThroughPort()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            inputPinNames: new[] { "a" },
            outputPinNames: new[] { "y" },
            biasPinNames: new[] { "bias" },
            powerThreshold: Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(2, "one enumerated input produces exactly two rows");
        table.Rows.ShouldAllBe(row => row.InputBits.Count == 1 && row.InputBits.ContainsKey("a"),
            "the bias pin must not appear as an InputBits column");

        var inputOff = RowFor(table, false).Outputs["y"];
        inputOff.IsOne.ShouldBeTrue("bias alone leaves half the power at y (0.5 ≥ 0.25)");
        inputOff.Power.ShouldBe(0.5, PowerTolerance, "bias alone acts as the rest power");

        var inputOn = RowFor(table, true).Outputs["y"];
        inputOn.IsOne.ShouldBeFalse("bias + a interfere destructively at y");
        inputOn.Power.ShouldBe(0.0, PowerTolerance,
            "Δφ = 90° between the arms extinguishes the through port exactly");
    }

    [Fact]
    public async Task ExtractAsync_BiasAndInput_InterfereAccordingToTheSMatrix()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "a" },
            new[] { "y", "aux" },
            new[] { "bias" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        // Lossless circuit: the power extinguished at y reappears at the cross port.
        var on = RowFor(table, true);
        on.Outputs["y"].Power.ShouldBe(0.0, PowerTolerance);
        on.Outputs["aux"].Power.ShouldBe(2.0, PowerTolerance,
            "both inputs' power exits through aux when y is extinguished");

        var off = RowFor(table, false);
        off.Outputs["y"].Power.ShouldBe(0.5, PowerTolerance);
        off.Outputs["aux"].Power.ShouldBe(0.5, PowerTolerance,
            "the bias alone splits equally between y and aux");
    }

    [Fact]
    public async Task ExtractAsync_BiasOnCombiner_BehavesLikeAnAlwaysOnInput()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            inputPinNames: new[] { "a" },
            outputPinNames: new[] { "y" },
            biasPinNames: new[] { "b" },
            Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        RowFor(table, false).Outputs["y"].Power.ShouldBe(0.5, PowerTolerance,
            "the bias alone splits half its power to y");
        RowFor(table, true).Outputs["y"].Power.ShouldBe(1.0, PowerTolerance,
            "a coherent bias recombines with the input into full power");
    }

    [Fact]
    public async Task ExtractAsync_NullBiasList_MatchesTheBiasFreeOverload()
    {
        var powers = await ExtractTwice(
            group => new TruthTableExtractor().ExtractAsync(
                group, new[] { "a" }, new[] { "y" }, Threshold025, LogicGateFixtureFactory.WavelengthNm),
            group => new TruthTableExtractor().ExtractAsync(
                group, new[] { "a" }, new[] { "y" }, biasPinNames: null, Threshold025, LogicGateFixtureFactory.WavelengthNm));

        powers.second.ShouldBe(powers.first,
            "an explicit null bias list must reproduce the plain overload exactly");
    }

    [Fact]
    public async Task ExtractAsync_EmptyBiasList_IsAPlainExtraction()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group, new[] { "a" }, new[] { "y" }, Array.Empty<string>(),
            Threshold025, LogicGateFixtureFactory.WavelengthNm);

        table.BiasPinNames.ShouldBeEmpty();
        RowFor(table, false).Outputs["y"].Power.ShouldBe(0.0, PowerTolerance,
            "no bias is held on when the bias list is empty");
    }

    [Fact]
    public async Task ExtractAsync_ResultCarriesBiasContext()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group, new[] { "a" }, new[] { "y" }, new[] { "bias" },
            Threshold025, LogicGateFixtureFactory.WavelengthNm);

        table.BiasPinNames.ShouldBe(new[] { "bias" });
        table.InputPinNames.ShouldBe(new[] { "a" });
        table.OutputPinNames.ShouldBe(new[] { "y" });
    }

    [Fact]
    public async Task ExtractAsync_BiasPinsDoNotCountAgainstTheInputLimit()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            inputPinNames: Enumerable.Range(0, TruthTableExtractor.MaxLogicInputs)
                .Select(i => i == 0 ? "a" : $"aux").ToArray(),
            outputPinNames: new[] { "y" },
            biasPinNames: new[] { "bias" },
            powerThreshold: Threshold025,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(1 << TruthTableExtractor.MaxLogicInputs,
            "the bias pin is held on, not enumerated");
    }

    [Fact]
    public async Task ExtractAsync_BiasOverlappingInput_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            new TruthTableExtractor().ExtractAsync(
                group, new[] { "a", "bias" }, new[] { "y" }, new[] { "bias" },
                Threshold025, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("biasPinNames");
        exception.Message.ShouldContain("'bias'");
    }

    [Fact]
    public async Task ExtractAsync_BiasOverlappingOutput_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            new TruthTableExtractor().ExtractAsync(
                group, new[] { "a" }, new[] { "y" }, new[] { "y" },
                Threshold025, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("biasPinNames");
        exception.Message.ShouldContain("'y'");
    }

    [Fact]
    public async Task ExtractAsync_DuplicateBiasPin_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            new TruthTableExtractor().ExtractAsync(
                group, new[] { "a" }, new[] { "y" }, new[] { "bias", "bias" },
                Threshold025, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("biasPinNames");
        exception.Message.ShouldContain("'bias'");
    }

    [Fact]
    public async Task ExtractAsync_UnknownBiasPin_ThrowsArgumentExceptionNamingThePin()
    {
        var group = LogicGateFixtureFactory.CreateNotMziGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() =>
            new TruthTableExtractor().ExtractAsync(
                group, new[] { "a" }, new[] { "y" }, new[] { "does-not-exist" },
                Threshold025, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("biasPinNames");
        exception.Message.ShouldContain("'does-not-exist'");
    }

    /// <summary>Runs two extraction lambdas over a fresh combiner group each.</summary>
    private static async Task<(double[] first, double[] second)> ExtractTwice(
        Func<CAP_Core.Components.Core.ComponentGroup, Task<TruthTable>> first,
        Func<CAP_Core.Components.Core.ComponentGroup, Task<TruthTable>> second)
    {
        var firstTable = await first(LogicGateFixtureFactory.CreateCombinerGroup());
        var secondTable = await second(LogicGateFixtureFactory.CreateCombinerGroup());
        return (PowersOf(firstTable), PowersOf(secondTable));
    }

    private static double[] PowersOf(TruthTable table) =>
        table.Rows.Select(r => r.Outputs["y"].Power).ToArray();

    /// <summary>Finds the row whose single input bit "a" has the given level.</summary>
    private static TruthTableRow RowFor(TruthTable table, bool inputLevel) =>
        table.Rows.Single(row => row.InputBits["a"] == inputLevel);
}

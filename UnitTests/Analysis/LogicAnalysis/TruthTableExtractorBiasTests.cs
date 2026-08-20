using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Bias-pin behavior of <see cref="TruthTableExtractor"/> (rung 4 of the NAND game):
/// a bias pin is constantly "on" in every row — coherent like an active input — but
/// never appears as an input-bit column. On the combiner fixture a bias alone puts
/// half its power (0.5) at the output; a bias plus a single enumerated input
/// recombine coherently into full power (1.0), exactly as the coupler S-matrix
/// dictates.
/// </summary>
public class TruthTableExtractorBiasTests
{
    private static readonly string[] Inputs = { "a" };
    private static readonly string[] Biases = { "b" };
    private static readonly string[] Outputs = { "y" };
    private const double Threshold = 0.75;
    private const double PowerTolerance = 1e-6;

    [Fact]
    public async Task ExtractAsync_BiasAlone_DeliversRestingPowerWithoutBitColumn()
    {
        var table = await Extract();

        table.BiasPinNames.ShouldBe(Biases);
        var row = table.Rows[0];
        row.InputBits.Keys.ShouldBe(Inputs, "bias pins never become input-bit columns");
        row.InputBits["a"].ShouldBeFalse();
        row.Outputs["y"].Power.ShouldBe(0.5, PowerTolerance,
            "one always-on bias source through the 50/50 coupler rests at half power");
        row.Outputs["y"].IsOne.ShouldBeFalse(
            "at threshold 0.75 the resting power is below the gate threshold");
    }

    [Fact]
    public async Task ExtractAsync_BiasPlusInput_InterferePerSMatrix()
    {
        var table = await Extract();

        var row = table.Rows[1];
        row.Outputs["y"].Power.ShouldBe(1.0, PowerTolerance,
            "|through·a + cross·b|² = |√0.5 + i·√0.5|² = 1.0 by the coupler S-matrix");
        row.Outputs["y"].IsOne.ShouldBeTrue("both sources together cross the threshold");
    }

    [Fact]
    public async Task ExtractAsync_EmptyBiasList_MatchesTheClassicOverload()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group, Inputs, Outputs, Array.Empty<string>(), Threshold, LogicGateFixtureFactory.WavelengthNm);

        table.BiasPinNames.ShouldBeEmpty();
        table.Rows[0].Outputs["y"].Power.ShouldBe(0.0, PowerTolerance);
        table.Rows[1].Outputs["y"].Power.ShouldBe(0.5, PowerTolerance);
    }

    [Fact]
    public async Task ExtractAsync_BiasOverlapsInput_ThrowsArgumentException()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(() => ToTask(new[] { "a" }));
        exception.Message.ShouldContain("'a'");
    }

    [Fact]
    public async Task ExtractAsync_BiasOverlapsOutput_ThrowsArgumentException()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(() => ToTask(new[] { "y" }));
        exception.Message.ShouldContain("'y'");
    }

    [Fact]
    public async Task ExtractAsync_DuplicateBias_ThrowsArgumentException()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(() => ToTask(new[] { "b", "b" }));
        exception.Message.ShouldContain("'b'");
    }

    [Fact]
    public async Task ExtractAsync_UnknownBias_ThrowsArgumentExceptionNamingThePin()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(() => ToTask(new[] { "no-such-pin" }));
        exception.Message.ShouldContain("'no-such-pin'");
    }

    private static async Task<TruthTable> Extract()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        return await new TruthTableExtractor().ExtractAsync(
            group, Inputs, Outputs, Biases, Threshold, LogicGateFixtureFactory.WavelengthNm);
    }

    private static Task<TruthTable> ToTask(string[] biases)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        return new TruthTableExtractor().ExtractAsync(
            group, Inputs, Outputs, biases, Threshold, LogicGateFixtureFactory.WavelengthNm);
    }
}

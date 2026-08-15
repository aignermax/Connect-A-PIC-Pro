using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Boundary and validation behavior of <see cref="TruthTableExtractor"/>: the gate
/// contract (pin lists, threshold, wavelength) fails loudly with clear exceptions
/// instead of silently simulating nonsense.
/// </summary>
public class TruthTableExtractorValidationTests
{
    private static readonly string[] Inputs = { "a", "b" };
    private static readonly string[] Outputs = { "y" };
    private const double Threshold = 0.25;

    [Fact]
    public async Task ExtractAsync_NullGroup_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => new TruthTableExtractor().ExtractAsync(
            null!, Inputs, Outputs, Threshold, LogicGateFixtureFactory.WavelengthNm));
    }

    [Fact]
    public async Task ExtractAsync_EmptyInputList_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, Array.Empty<string>(), Outputs, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("inputPinNames");
    }

    [Fact]
    public async Task ExtractAsync_EmptyOutputList_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, Inputs, Array.Empty<string>(), Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.ParamName.ShouldBe("outputPinNames");
    }

    [Fact]
    public async Task ExtractAsync_MoreThanMaxInputs_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var fiveInputs = new[] { "in0", "in1", "in2", "in3", "in4" };

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, fiveInputs, Outputs, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.Message.ShouldContain(TruthTableExtractor.MaxLogicInputs.ToString());
    }

    [Fact]
    public async Task ExtractAsync_ExactlyMaxInputs_IsAccepted()
    {
        var group = LogicGateFixtureFactory.CreateFourBitBusGroup();

        var table = await new TruthTableExtractor().ExtractAsync(
            group,
            new[] { "in0", "in1", "in2", "in3" },
            new[] { "out0" },
            Threshold,
            LogicGateFixtureFactory.WavelengthNm);

        table.Rows.Count.ShouldBe(16);
    }

    [Fact]
    public async Task ExtractAsync_DuplicateInputPin_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, new[] { "a", "a" }, Outputs, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.Message.ShouldContain("'a'");
    }

    [Fact]
    public async Task ExtractAsync_SamePinAsInputAndOutput_ThrowsArgumentException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, new[] { "a" }, new[] { "a" }, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.Message.ShouldContain("'a'");
    }

    [Fact]
    public async Task ExtractAsync_UnknownInputPin_ThrowsArgumentExceptionNamingThePin()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, new[] { "a", "does-not-exist" }, Outputs, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.Message.ShouldContain("'does-not-exist'");
    }

    [Fact]
    public async Task ExtractAsync_UnknownOutputPin_ThrowsArgumentExceptionNamingThePin()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var exception = await Should.ThrowAsync<ArgumentException>(() => new TruthTableExtractor().ExtractAsync(
            group, new[] { "a" }, new[] { "does-not-exist" }, Threshold, LogicGateFixtureFactory.WavelengthNm));

        exception.Message.ShouldContain("'does-not-exist'");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    public async Task ExtractAsync_ThresholdOutsideOpenInterval_ThrowsArgumentOutOfRangeException(double threshold)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new TruthTableExtractor().ExtractAsync(
            group, Inputs, Outputs, threshold, LogicGateFixtureFactory.WavelengthNm));
    }

    [Fact]
    public async Task ExtractAsync_NonPositiveWavelength_ThrowsArgumentOutOfRangeException()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new TruthTableExtractor().ExtractAsync(
            group, Inputs, Outputs, Threshold, wavelengthNm: 0));
    }
}

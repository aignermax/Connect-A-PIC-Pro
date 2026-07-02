using CAP_Core.Analysis.EyeDiagram;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

public class PrbsGeneratorTests
{
    [Theory]
    [InlineData(PrbsOrder.Prbs7, 127)]
    [InlineData(PrbsOrder.Prbs11, 2047)]
    [InlineData(PrbsOrder.Prbs23, 8_388_607)]
    public void PatternLength_MatchesTwoToTheNMinusOne(PrbsOrder order, int expected)
    {
        PrbsGenerator.PatternLength(order).ShouldBe(expected);
    }

    [Fact]
    public void GenerateBits_Prbs7_IsBalancedOverOnePeriod()
    {
        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 127);

        // Maximal-length LFSR: 2^(n-1) ones and 2^(n-1) - 1 zeros per period.
        bits.Count(b => b).ShouldBe(64);
        bits.Count(b => !b).ShouldBe(63);
    }

    [Fact]
    public void GenerateBits_Prbs7_RepeatsWithPeriod127()
    {
        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 254);

        for (int i = 0; i < 127; i++)
            bits[i + 127].ShouldBe(bits[i], $"bit {i} differs from bit {i + 127}");
    }

    [Fact]
    public void GenerateBits_Prbs7_DoesNotRepeatEarlierThanFullPeriod()
    {
        var bits = PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 127);

        // A maximal-length LFSR has cyclic period exactly 127: no cyclic shift
        // of the period maps the sequence onto itself.
        bits.Distinct().Count().ShouldBe(2);
        Enumerable.Range(1, 126).Any(shift =>
            Enumerable.Range(0, 127).All(i => bits[i] == bits[(i + shift) % 127]))
            .ShouldBeFalse("sequence repeated with a cyclic period shorter than 127");
    }

    [Fact]
    public void GenerateBits_IsDeterministic()
    {
        var first = PrbsGenerator.GenerateBits(PrbsOrder.Prbs11, 500);
        var second = PrbsGenerator.GenerateBits(PrbsOrder.Prbs11, 500);

        first.ShouldBe(second);
    }

    [Fact]
    public void GenerateBits_InvalidBitCount_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => PrbsGenerator.GenerateBits(PrbsOrder.Prbs7, 0));
    }

    [Fact]
    public void ToNrzSamples_ExpandsEachBitToSamplesPerBit()
    {
        var bits = new[] { true, false, true };

        var samples = PrbsGenerator.ToNrzSamples(bits, samplesPerBit: 4, amplitude: 2.5);

        samples.Length.ShouldBe(12);
        samples.Take(4).ShouldAllBe(s => s == 2.5);
        samples.Skip(4).Take(4).ShouldAllBe(s => s == 0.0);
        samples.Skip(8).ShouldAllBe(s => s == 2.5);
    }

    [Fact]
    public void ToNrzSamples_InvalidSamplesPerBit_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => PrbsGenerator.ToNrzSamples(new[] { true }, 0, 1.0));
    }
}

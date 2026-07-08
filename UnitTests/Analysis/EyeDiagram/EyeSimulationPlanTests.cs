using CAP_Core.Analysis.EyeDiagram;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

public class EyeSimulationPlanTests
{
    private const double SampleRate = 12.5e12; // ~100 nm span around 1550 nm

    [Fact]
    public void Create_25Gbps_Yields500SamplesPerBit()
    {
        var plan = EyeSimulationPlan.Create(25e9, SampleRate, patternBits: 127);

        plan.SamplesPerBit.ShouldBe(500);
        plan.BitCount.ShouldBe(127);
        plan.TotalSamples.ShouldBe(127 * 500);
        plan.BitPeriodSeconds.ShouldBe(500 / SampleRate, 1e-20);
    }

    [Fact]
    public void Create_BitPeriodAlignsWithSampleGrid()
    {
        var plan = EyeSimulationPlan.Create(30e9, SampleRate, patternBits: 127);

        // Bit period must be an integer number of samples so folding is exact.
        (plan.BitPeriodSeconds * SampleRate).ShouldBe(plan.SamplesPerBit, 1e-9);
    }

    [Fact]
    public void Create_PatternExceedingSampleBudget_IsTruncated()
    {
        int prbs23Length = PrbsGenerator.PatternLength(PrbsOrder.Prbs23);

        var plan = EyeSimulationPlan.Create(25e9, SampleRate, prbs23Length);

        plan.BitCount.ShouldBeLessThan(prbs23Length);
        plan.TotalSamples.ShouldBeLessThanOrEqualTo(EyeSimulationPlan.MaxTotalSamples);
    }

    [Fact]
    public void Create_BitRateAboveBandwidth_Throws()
    {
        // 5 Tbps on a 12.5 THz grid → 2.5 samples/bit → too few.
        Should.Throw<InvalidOperationException>(
            () => EyeSimulationPlan.Create(5e12, SampleRate, 127));
    }

    [Fact]
    public void Create_BitRateTooLowForSampleBudget_Throws()
    {
        // 1 Mbps → 12.5e6 samples/bit → fewer than MinBits fit into the budget.
        Should.Throw<InvalidOperationException>(
            () => EyeSimulationPlan.Create(1e6, SampleRate, 127));
    }

    [Theory]
    [InlineData(0, SampleRate, 127)]
    [InlineData(25e9, 0, 127)]
    [InlineData(25e9, SampleRate, 0)]
    public void Create_InvalidArguments_Throw(double bitRate, double sampleRate, int patternBits)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => EyeSimulationPlan.Create(bitRate, sampleRate, patternBits));
    }
}

using CAP_Core.Analysis.MonteCarloAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis
{
    public class MonteCarloResultTests
    {
        private static MonteCarloResult CreateResult()
            => new(
                nominalCurve: new double[] { 1.0, 2.0 },
                runCurves: new IReadOnlyList<double>[]
                {
                    new double[] { 0.8, 2.4 },
                    new double[] { 1.2, 1.6 },
                    new double[] { 1.0, 2.0 },
                    new double[] { 0.6, 2.8 },
                });

        [Fact]
        public void Constructor_RejectsMismatchedCurveLengths()
        {
            Should.Throw<ArgumentException>(() => new MonteCarloResult(
                new double[] { 1.0 },
                new IReadOnlyList<double>[] { new double[] { 1.0, 2.0 } }));
        }

        [Fact]
        public void GetMinAndMaxCurves_FormTheEnvelope()
        {
            var result = CreateResult();

            result.GetMinCurve().ShouldBe(new[] { 0.6, 1.6 });
            result.GetMaxCurve().ShouldBe(new[] { 1.2, 2.8 });
        }

        [Fact]
        public void GetMeanCurve_AveragesPerIndex()
        {
            var result = CreateResult();

            var mean = result.GetMeanCurve();
            mean[0].ShouldBe(0.9, 1e-12);
            mean[1].ShouldBe(2.2, 1e-12);
        }

        [Fact]
        public void GetPercentileCurve_InterpolatesBetweenOrderStatistics()
        {
            var result = CreateResult();

            // Sorted first index: 0.6, 0.8, 1.0, 1.2 → median = (0.8 + 1.0)/2.
            result.GetPercentileCurve(50)[0].ShouldBe(0.9, 1e-12);
            result.GetPercentileCurve(0)[0].ShouldBe(0.6);
            result.GetPercentileCurve(100)[0].ShouldBe(1.2);
        }

        [Fact]
        public void GetPercentileCurve_RejectsOutOfRangePercentile()
        {
            var result = CreateResult();

            Should.Throw<ArgumentOutOfRangeException>(() => result.GetPercentileCurve(-1));
            Should.Throw<ArgumentOutOfRangeException>(() => result.GetPercentileCurve(101));
        }

        [Fact]
        public void GetSamplesAtIndex_ReturnsColumnAcrossRuns()
        {
            var result = CreateResult();

            result.GetSamplesAtIndex(1).ShouldBe(new[] { 2.4, 1.6, 2.0, 2.8 });
        }
    }

    public class DistributionHistogramTests
    {
        [Fact]
        public void Create_CountsAllSamplesIntoBins()
        {
            var samples = new double[] { 0.0, 0.1, 0.5, 0.9, 1.0 };

            var histogram = DistributionHistogram.Create(samples, binCount: 10);

            histogram.BinCounts.Sum().ShouldBe(samples.Length);
            histogram.MinValue.ShouldBe(0.0);
            histogram.MaxValue.ShouldBe(1.0);
        }

        [Fact]
        public void Create_IdenticalSamples_YieldsSingleFullBin()
        {
            var histogram = DistributionHistogram.Create(new double[] { 0.7, 0.7, 0.7 });

            histogram.BinCounts.ShouldBe(new[] { 3 });
        }

        [Fact]
        public void Create_MaxSampleFallsIntoLastBin()
        {
            var histogram = DistributionHistogram.Create(new double[] { 0, 1 }, binCount: 4);

            histogram.BinCounts[0].ShouldBe(1);
            histogram.BinCounts[3].ShouldBe(1);
        }

        [Fact]
        public void BinCenter_ReturnsMidpointOfBin()
        {
            var histogram = DistributionHistogram.Create(new double[] { 0, 1 }, binCount: 2);

            histogram.BinCenter(0).ShouldBe(0.25);
            histogram.BinCenter(1).ShouldBe(0.75);
        }

        [Fact]
        public void Create_RejectsEmptySamples()
        {
            Should.Throw<ArgumentException>(() => DistributionHistogram.Create(Array.Empty<double>()));
        }
    }

    public class MonteCarloConfigurationTests
    {
        [Fact]
        public void Defaults_MatchIssueRequirements()
        {
            var config = new MonteCarloConfiguration();

            config.RunCount.ShouldBe(1000);
            config.Seed.ShouldBe(42);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Constructor_RejectsInvalidRunCount(int runCount)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => new MonteCarloConfiguration(runCount: runCount));
        }
    }
}

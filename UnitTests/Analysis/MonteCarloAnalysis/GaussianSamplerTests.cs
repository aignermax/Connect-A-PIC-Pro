using CAP_Core.Analysis.MonteCarloAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis
{
    public class GaussianSamplerTests
    {
        [Fact]
        public void SameSeed_ProducesIdenticalSequence()
        {
            var first = new GaussianSampler(1234);
            var second = new GaussianSampler(1234);

            for (int i = 0; i < 100; i++)
                second.NextGaussian().ShouldBe(first.NextGaussian());
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var first = new GaussianSampler(1);
            var second = new GaussianSampler(2);

            var firstSamples = Enumerable.Range(0, 10).Select(_ => first.NextGaussian()).ToArray();
            var secondSamples = Enumerable.Range(0, 10).Select(_ => second.NextGaussian()).ToArray();

            firstSamples.ShouldNotBe(secondSamples);
        }

        [Fact]
        public void Samples_ApproximateStandardNormalDistribution()
        {
            var sampler = new GaussianSampler(42);
            const int count = 100_000;

            var samples = Enumerable.Range(0, count).Select(_ => sampler.NextGaussian()).ToArray();
            double mean = samples.Average();
            double variance = samples.Sum(s => (s - mean) * (s - mean)) / count;

            mean.ShouldBe(0.0, 0.02);
            variance.ShouldBe(1.0, 0.02);
        }

        [Fact]
        public void Samples_SmallDeviationsAreMoreLikelyThanLargeOnes()
        {
            var sampler = new GaussianSampler(7);
            const int count = 10_000;

            var samples = Enumerable.Range(0, count).Select(_ => sampler.NextGaussian()).ToArray();
            int withinOneSigma = samples.Count(s => Math.Abs(s) <= 1.0);
            int beyondTwoSigma = samples.Count(s => Math.Abs(s) > 2.0);

            // ~68 % within 1σ, ~4.6 % beyond 2σ for a standard normal.
            withinOneSigma.ShouldBeGreaterThan((int)(count * 0.65));
            beyondTwoSigma.ShouldBeLessThan((int)(count * 0.07));
        }
    }
}

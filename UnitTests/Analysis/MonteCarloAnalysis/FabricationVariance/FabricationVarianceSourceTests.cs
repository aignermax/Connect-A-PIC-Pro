using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis.FabricationVariance
{
    public class FabricationVarianceSourceTests
    {
        private static FabricationVarianceSource CreateSource(int componentCount = 2)
        {
            var components = new List<Component>();
            for (int i = 0; i < componentCount; i++)
                components.Add(TestComponentFactory.CreateStraightWaveGuide());
            return new FabricationVarianceSource(components, ProcessTolerances.Default);
        }

        [Fact]
        public void NewSource_StartsNominal()
        {
            CreateSource().CurrentDeviations.ShouldBeNull();
        }

        [Fact]
        public void ApplyVariance_ProducesOneDeviationPerComponent()
        {
            var source = CreateSource(componentCount: 3);

            source.ApplyVariance(new GaussianSampler(1));

            source.CurrentDeviations.ShouldNotBeNull();
            source.CurrentDeviations!.Count.ShouldBe(3);
        }

        [Fact]
        public void RestoreNominal_ClearsTheActiveSample()
        {
            var source = CreateSource();
            source.ApplyVariance(new GaussianSampler(1));

            source.RestoreNominal();

            source.CurrentDeviations.ShouldBeNull();
        }

        [Fact]
        public void ApplyVariance_SameSeed_ReproducesIdenticalDeviations()
        {
            var components = new List<Component>
            {
                TestComponentFactory.CreateStraightWaveGuide(),
                TestComponentFactory.CreateStraightWaveGuide(),
            };
            var first = new FabricationVarianceSource(components, ProcessTolerances.Default);
            var second = new FabricationVarianceSource(components, ProcessTolerances.Default);

            first.ApplyVariance(new GaussianSampler(7));
            second.ApplyVariance(new GaussianSampler(7));

            foreach (var component in components)
                second.CurrentDeviations![component].ShouldBe(first.CurrentDeviations![component]);
        }

        [Fact]
        public void ApplyVariance_ComponentsShareTheWaferLevelDeviation()
        {
            // With a local term of only 20% of the wafer sigma, two components on the
            // same wafer must be strongly correlated: corr = 1/(1+0.2²) ≈ 0.96.
            var componentA = TestComponentFactory.CreateStraightWaveGuide();
            var componentB = TestComponentFactory.CreateStraightWaveGuide();
            var source = new FabricationVarianceSource(
                new[] { componentA, componentB }, ProcessTolerances.Default);
            var sampler = new GaussianSampler(42);

            var samplesA = new List<double>();
            var samplesB = new List<double>();
            const int Runs = 500;
            for (int i = 0; i < Runs; i++)
            {
                source.ApplyVariance(sampler);
                samplesA.Add(source.CurrentDeviations![componentA].DeltaWidthNm);
                samplesB.Add(source.CurrentDeviations![componentB].DeltaWidthNm);
            }

            Correlation(samplesA, samplesB).ShouldBeGreaterThan(0.8);
        }

        [Fact]
        public void ApplyVariance_LocalTermMakesComponentsDiffer()
        {
            var componentA = TestComponentFactory.CreateStraightWaveGuide();
            var componentB = TestComponentFactory.CreateStraightWaveGuide();
            var source = new FabricationVarianceSource(
                new[] { componentA, componentB }, ProcessTolerances.Default);

            source.ApplyVariance(new GaussianSampler(1));

            source.CurrentDeviations![componentA]
                .ShouldNotBe(source.CurrentDeviations[componentB]);
        }

        [Fact]
        public void ApplyVariance_DeviationsScaleWithTolerances()
        {
            var component = TestComponentFactory.CreateStraightWaveGuide();
            var narrow = new FabricationVarianceSource(
                new[] { component }, new ProcessTolerances(1, 1));
            var wide = new FabricationVarianceSource(
                new[] { component }, new ProcessTolerances(10, 10));

            narrow.ApplyVariance(new GaussianSampler(5));
            var narrowDeviation = narrow.CurrentDeviations![component];
            wide.ApplyVariance(new GaussianSampler(5));
            var wideDeviation = wide.CurrentDeviations![component];

            wideDeviation.DeltaWidthNm.ShouldBe(10 * narrowDeviation.DeltaWidthNm, 1e-9);
            wideDeviation.DeltaThicknessNm.ShouldBe(10 * narrowDeviation.DeltaThicknessNm, 1e-9);
        }

        private static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            double meanA = a.Average(), meanB = b.Average();
            double covariance = 0, varianceA = 0, varianceB = 0;
            for (int i = 0; i < a.Count; i++)
            {
                covariance += (a[i] - meanA) * (b[i] - meanB);
                varianceA += (a[i] - meanA) * (a[i] - meanA);
                varianceB += (b[i] - meanB) * (b[i] - meanB);
            }
            return covariance / Math.Sqrt(varianceA * varianceB);
        }
    }
}

using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis
{
    public class MonteCarloRunnerTests
    {
        /// <summary>
        /// Variance-source stub: "applying" a sample stores one Gaussian draw as the
        /// current state; the sampler echoes that state as the metric curve.
        /// </summary>
        private sealed class StubVarianceSource : IVarianceSource
        {
            public double CurrentOffset { get; private set; }
            public int ApplyCalls { get; private set; }
            public bool IsNominal { get; private set; } = true;

            public void ApplyVariance(GaussianSampler sampler)
            {
                ApplyCalls++;
                IsNominal = false;
                CurrentOffset = sampler.NextGaussian();
            }

            public void RestoreNominal()
            {
                IsNominal = true;
                CurrentOffset = 0;
            }
        }

        private static Func<CancellationToken, Task<double[]>> EchoOffset(StubVarianceSource source)
            => _ => Task.FromResult(new[] { source.CurrentOffset });

        [Fact]
        public async Task RunAsync_FixedSeed_ReproducesIdenticalResults()
        {
            var config = new MonteCarloConfiguration(runCount: 25, seed: 42);
            var runner = new MonteCarloRunner();
            var sourceA = new StubVarianceSource();
            var sourceB = new StubVarianceSource();

            var first = await runner.RunAsync(config, sourceA, EchoOffset(sourceA));
            var second = await runner.RunAsync(config, sourceB, EchoOffset(sourceB));

            for (int run = 0; run < config.RunCount; run++)
                second.RunCurves[run][0].ShouldBe(first.RunCurves[run][0]);
        }

        [Fact]
        public async Task RunAsync_NominalCurve_IsSampledBeforeAnyVariance()
        {
            var config = new MonteCarloConfiguration(runCount: 5, seed: 1);
            var source = new StubVarianceSource();

            var result = await new MonteCarloRunner().RunAsync(config, source, EchoOffset(source));

            result.NominalCurve[0].ShouldBe(0.0);
        }

        [Fact]
        public async Task RunAsync_RestoresNominal_AfterCompletion()
        {
            var config = new MonteCarloConfiguration(runCount: 10, seed: 3);
            var source = new StubVarianceSource();

            await new MonteCarloRunner().RunAsync(config, source, EchoOffset(source));

            source.IsNominal.ShouldBeTrue();
            source.ApplyCalls.ShouldBe(10);
        }

        [Fact]
        public async Task RunAsync_Cancellation_ThrowsAndRestoresNominal()
        {
            var config = new MonteCarloConfiguration(runCount: 100, seed: 5);
            var source = new StubVarianceSource();
            using var cts = new CancellationTokenSource();

            int runsExecuted = 0;
            Func<CancellationToken, Task<double[]>> sampler = _ =>
            {
                if (++runsExecuted == 4) cts.Cancel();
                return Task.FromResult(new[] { source.CurrentOffset });
            };

            await Should.ThrowAsync<OperationCanceledException>(
                () => new MonteCarloRunner().RunAsync(config, source, sampler, null, cts.Token));

            runsExecuted.ShouldBeLessThan(10);
            source.IsNominal.ShouldBeTrue();
        }

        [Fact]
        public async Task RunAsync_ReportsProgressForEveryRun()
        {
            var config = new MonteCarloConfiguration(runCount: 8, seed: 2);
            var source = new StubVarianceSource();
            var reports = new List<MonteCarloProgress>();
            var progress = new SynchronousProgress(reports.Add);

            await new MonteCarloRunner().RunAsync(config, source, EchoOffset(source), progress);

            reports.Count.ShouldBe(8);
            reports[^1].CompletedRuns.ShouldBe(8);
            reports[^1].TotalRuns.ShouldBe(8);
        }

        [Fact]
        public async Task RunAsync_VariedRuns_SpreadAroundNominal()
        {
            var config = new MonteCarloConfiguration(runCount: 200, seed: 42);
            var source = new StubVarianceSource();

            var result = await new MonteCarloRunner().RunAsync(config, source, EchoOffset(source));

            var samples = result.GetSamplesAtIndex(0);
            samples.Average().ShouldBe(0.0, 0.1);
            samples.Distinct().Count().ShouldBeGreaterThan(100);
        }

        /// <summary>Progress sink that invokes the callback inline (no SynchronizationContext post).</summary>
        private sealed class SynchronousProgress : IProgress<MonteCarloProgress>
        {
            private readonly Action<MonteCarloProgress> _callback;
            public SynchronousProgress(Action<MonteCarloProgress> callback) => _callback = callback;
            public void Report(MonteCarloProgress value) => _callback(value);
        }
    }
}

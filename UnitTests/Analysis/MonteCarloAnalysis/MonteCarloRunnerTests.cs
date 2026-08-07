using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis
{
    public class MonteCarloRunnerTests
    {
        private static Slider CreateSlider(double value = 0.5)
            => new(Guid.NewGuid(), 0, value, maxValue: 1, minValue: 0);

        /// <summary>Sampler stub that reports the slider's current value as the "curve".</summary>
        private static Func<CancellationToken, Task<double[]>> EchoSlider(Slider slider)
            => _ => Task.FromResult(new[] { slider.Value });

        [Fact]
        public async Task RunAsync_FixedSeed_ReproducesIdenticalResults()
        {
            var slider = CreateSlider();
            var config = new MonteCarloConfiguration(runCount: 25, sigmaRelative: 0.05, seed: 42);
            var runner = new MonteCarloRunner();

            var first = await runner.RunAsync(config, new[] { slider }, EchoSlider(slider));
            var second = await runner.RunAsync(config, new[] { slider }, EchoSlider(slider));

            for (int run = 0; run < config.RunCount; run++)
                second.RunCurves[run][0].ShouldBe(first.RunCurves[run][0]);
        }

        [Fact]
        public async Task RunAsync_NominalCurve_IsUnjittered()
        {
            var slider = CreateSlider(0.5);
            var config = new MonteCarloConfiguration(runCount: 5, sigmaRelative: 0.1, seed: 1);

            var result = await new MonteCarloRunner().RunAsync(config, new[] { slider }, EchoSlider(slider));

            result.NominalCurve[0].ShouldBe(0.5);
        }

        [Fact]
        public async Task RunAsync_RestoresSliderValues_AfterCompletion()
        {
            var slider = CreateSlider(0.5);
            var config = new MonteCarloConfiguration(runCount: 10, sigmaRelative: 0.2, seed: 3);

            await new MonteCarloRunner().RunAsync(config, new[] { slider }, EchoSlider(slider));

            slider.Value.ShouldBe(0.5);
        }

        [Fact]
        public async Task RunAsync_Cancellation_ThrowsAndRestoresSliders()
        {
            var slider = CreateSlider(0.5);
            var config = new MonteCarloConfiguration(runCount: 100, sigmaRelative: 0.1, seed: 5);
            using var cts = new CancellationTokenSource();

            int runsExecuted = 0;
            Func<CancellationToken, Task<double[]>> sampler = _ =>
            {
                if (++runsExecuted == 4) cts.Cancel();
                return Task.FromResult(new[] { slider.Value });
            };

            await Should.ThrowAsync<OperationCanceledException>(
                () => new MonteCarloRunner().RunAsync(config, new[] { slider }, sampler, null, cts.Token));

            runsExecuted.ShouldBeLessThan(10);
            slider.Value.ShouldBe(0.5);
        }

        [Fact]
        public async Task RunAsync_ReportsProgressForEveryRun()
        {
            var slider = CreateSlider();
            var config = new MonteCarloConfiguration(runCount: 8, sigmaRelative: 0.05, seed: 2);
            var reports = new List<MonteCarloProgress>();
            var progress = new SynchronousProgress(reports.Add);

            await new MonteCarloRunner().RunAsync(config, new[] { slider }, EchoSlider(slider), progress);

            reports.Count.ShouldBe(8);
            reports[^1].CompletedRuns.ShouldBe(8);
            reports[^1].TotalRuns.ShouldBe(8);
        }

        [Fact]
        public async Task RunAsync_JitteredRuns_SpreadAroundNominal()
        {
            var slider = CreateSlider(0.5);
            var config = new MonteCarloConfiguration(runCount: 200, sigmaRelative: 0.05, seed: 42);

            var result = await new MonteCarloRunner().RunAsync(config, new[] { slider }, EchoSlider(slider));

            var samples = result.GetSamplesAtIndex(0);
            samples.Average().ShouldBe(0.5, 0.01);
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

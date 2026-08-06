using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis
{
    public class SliderJitterTests
    {
        private static Slider CreateSlider(double value, double min = 0, double max = 1)
            => new(Guid.NewGuid(), 0, value, max, min);

        [Fact]
        public void ApplyJitter_ChangesSliderValuesAroundNominal()
        {
            var slider = CreateSlider(0.5);
            var jitter = new SliderJitter(new[] { slider });

            jitter.ApplyJitter(new GaussianSampler(42), sigmaRelative: 0.1);

            slider.Value.ShouldNotBe(0.5);
            slider.Value.ShouldBeInRange(0, 1);
        }

        [Fact]
        public void ApplyJitter_ClampsToSliderBounds()
        {
            var slider = CreateSlider(0.95);
            var jitter = new SliderJitter(new[] { slider });

            // Huge sigma forces many samples outside the range → all must be clamped.
            var sampler = new GaussianSampler(1);
            for (int i = 0; i < 200; i++)
            {
                jitter.ApplyJitter(sampler, sigmaRelative: 10.0);
                slider.Value.ShouldBeInRange(0, 1);
            }
        }

        [Fact]
        public void ApplyJitter_WithZeroSigma_KeepsNominalValues()
        {
            var slider = CreateSlider(0.3);
            var jitter = new SliderJitter(new[] { slider });

            jitter.ApplyJitter(new GaussianSampler(42), sigmaRelative: 0.0);

            slider.Value.ShouldBe(0.3);
        }

        [Fact]
        public void RestoreNominal_RevertsAllSliders()
        {
            var sliders = new[] { CreateSlider(0.2), CreateSlider(0.8) };
            var jitter = new SliderJitter(sliders);

            jitter.ApplyJitter(new GaussianSampler(42), sigmaRelative: 0.2);
            jitter.RestoreNominal();

            sliders[0].Value.ShouldBe(0.2);
            sliders[1].Value.ShouldBe(0.8);
        }

        [Fact]
        public void SameSeed_ProducesIdenticalJitterSequence()
        {
            var sliderA = CreateSlider(0.5);
            var sliderB = CreateSlider(0.5);

            var jitterA = new SliderJitter(new[] { sliderA });
            var jitterB = new SliderJitter(new[] { sliderB });
            var samplerA = new GaussianSampler(99);
            var samplerB = new GaussianSampler(99);

            for (int i = 0; i < 20; i++)
            {
                jitterA.ApplyJitter(samplerA, 0.05);
                jitterB.ApplyJitter(samplerB, 0.05);
                sliderB.Value.ShouldBe(sliderA.Value);
            }
        }
    }
}

using CAP_Core.LightCalculation.TimeDomainSimulation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

public class TimeDomainConvolverTests
{
    [Fact]
    public void Convolve_WithUnitImpulse_ReturnsOriginalSignal()
    {
        // Arrange: input signal = Gaussian-like
        double[] signal = { 0, 0.1, 0.5, 1.0, 0.5, 0.1, 0 };
        // Impulse response = unit delta at t=0 (identity convolution)
        var h = new Complex[] { Complex.One, Complex.Zero, Complex.Zero, Complex.Zero };

        // Act
        var output = TimeDomainConvolver.Convolve(signal, h);

        // Assert: first signal.Length samples should match input
        for (int i = 0; i < signal.Length; i++)
            output[i].Real.ShouldBe(signal[i], 1e-9);
    }

    [Fact]
    public void Convolve_WithDelayedImpulse_ReturnsShiftedSignal()
    {
        // Arrange: impulse response = delta at sample index 2 (2-sample delay)
        double[] signal = { 0, 0, 1.0, 0.5, 0.25, 0, 0, 0 };
        var h = new Complex[] { Complex.Zero, Complex.Zero, Complex.One };

        // Act
        var output = TimeDomainConvolver.Convolve(signal, h);

        // Assert: output[4] should equal input[2] = 1.0
        output[4].Real.ShouldBe(1.0, 1e-9);
        // output[5] should equal input[3] = 0.5
        output[5].Real.ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void ConvolveToIntensity_ReturnsMagnitudeSquared()
    {
        // Arrange: unit delta impulse response
        double[] signal = { 0.0, 0.0, 3.0, 4.0, 0.0, 0.0 };
        var h = new Complex[] { Complex.One };

        // Act
        var intensity = TimeDomainConvolver.ConvolveToIntensity(signal, h);

        // Assert: intensity[2] = 3^2 = 9, intensity[3] = 4^2 = 16
        intensity[2].ShouldBe(9.0, 1e-9);
        intensity[3].ShouldBe(16.0, 1e-9);
    }

    [Fact]
    public void Convolve_NullSignal_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            TimeDomainConvolver.Convolve((double[])null!, new Complex[] { Complex.One }));
    }

    [Fact]
    public void Convolve_NullImpulseResponse_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            TimeDomainConvolver.Convolve(new double[] { 1.0 }, null!));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    public void NextPowerOfTwo_ReturnsExpectedValue(int input, int expected)
    {
        TimeDomainConvolver.NextPowerOfTwo(input).ShouldBe(expected);
    }

    [Fact]
    public void Convolve_HalfAmplitudeImpulse_ScalesOutput()
    {
        // Arrange: h = 0.5 * delta at t=0 (like a 50% coupler)
        double[] signal = { 0, 0, 4.0, 2.0, 0, 0 };
        var h = new Complex[] { new Complex(0.5, 0) };

        // Act
        var output = TimeDomainConvolver.Convolve(signal, h);

        // Assert: output should be 0.5 * input
        output[2].Real.ShouldBe(2.0, 1e-9);
        output[3].Real.ShouldBe(1.0, 1e-9);
    }
}

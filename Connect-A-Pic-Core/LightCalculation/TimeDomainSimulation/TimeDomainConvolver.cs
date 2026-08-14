using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// FFT-based fast linear convolution for time-domain signal processing.
/// Pads operands to the next power-of-two size to avoid circular aliasing.
/// </summary>
public static class TimeDomainConvolver
{
    /// <summary>
    /// Convolves a real input signal s(t) with a complex impulse response h(t)
    /// and returns the complex output field y(t) = s * h.
    /// Length of result = signal.Length + impulseResponse.Length - 1.
    /// </summary>
    /// <param name="signal">Real-valued input envelope samples.</param>
    /// <param name="impulseResponse">Complex impulse-response samples h[n].</param>
    public static Complex[] Convolve(double[] signal, Complex[] impulseResponse)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        return Convolve(signal.Select(v => new Complex(v, 0)).ToArray(), impulseResponse);
    }

    /// <summary>
    /// Convolves a complex input field s(t) — e.g. an envelope carrying a laser
    /// phase-noise factor e^{iφ(t)} (issue #834) — with a complex impulse response
    /// h(t) and returns y(t) = s * h. Because delay acts on the full complex input,
    /// each path's group delay is applied to the phase process automatically.
    /// Length of result = signal.Length + impulseResponse.Length - 1.
    /// </summary>
    /// <param name="signal">Complex input field samples.</param>
    /// <param name="impulseResponse">Complex impulse-response samples h[n].</param>
    public static Complex[] Convolve(Complex[] signal, Complex[] impulseResponse)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (impulseResponse == null) throw new ArgumentNullException(nameof(impulseResponse));

        int outputLength = signal.Length + impulseResponse.Length - 1;
        int fftSize = NextPowerOfTwo(outputLength);

        var sComplex = new Complex[fftSize];
        for (int i = 0; i < signal.Length; i++)
            sComplex[i] = signal[i];

        var hPadded = new Complex[fftSize];
        for (int i = 0; i < impulseResponse.Length; i++)
            hPadded[i] = impulseResponse[i];

        // Use NoScaling (unnormalized) on both transforms, then divide by fftSize.
        // This gives the standard linear-convolution result without the 1/√N artifact
        // that FourierOptions.Default (symmetric) would introduce.
        Fourier.Forward(sComplex, FourierOptions.NoScaling);
        Fourier.Forward(hPadded, FourierOptions.NoScaling);

        var product = new Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
            product[i] = sComplex[i] * hPadded[i];

        Fourier.Inverse(product, FourierOptions.NoScaling);

        // Normalise: IFFT_noscale(FFT_noscale(x)) = N*x, so divide by N
        double invN = 1.0 / fftSize;
        var result = new Complex[outputLength];
        for (int i = 0; i < outputLength; i++)
            result[i] = product[i] * invN;

        return result;
    }

    /// <summary>
    /// Computes the intensity envelope |y(t)|² after convolving signal with impulseResponse.
    /// Convenience wrapper that squares the magnitude of <see cref="Convolve"/>.
    /// </summary>
    public static double[] ConvolveToIntensity(double[] signal, Complex[] impulseResponse)
    {
        var field = Convolve(signal, impulseResponse);
        return field.Select(c => c.Real * c.Real + c.Imaginary * c.Imaginary).ToArray();
    }

    /// <summary>Returns the smallest power of two that is ≥ <paramref name="n"/>.</summary>
    public static int NextPowerOfTwo(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        int power = 1;
        while (power < n) power <<= 1;
        return power;
    }
}

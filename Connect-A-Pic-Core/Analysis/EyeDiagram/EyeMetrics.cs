namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Numeric summary of an eye diagram at the optimal sampling instant.
/// </summary>
/// <param name="QFactor">Q = (μ₁ − μ₀) / (σ₁ + σ₀) at the optimal sampling instant.</param>
/// <param name="BerEstimate">Estimated bit-error rate = ½·erfc(Q/√2).</param>
/// <param name="EyeHeight">Vertical eye opening (μ₁ − 3σ₁) − (μ₀ + 3σ₀) in trace amplitude units; ≤ 0 means closed.</param>
/// <param name="EyeWidthSeconds">Horizontal span of the bit period where the eye stays open (Q ≥ 3.09, i.e. BER ≤ 1e-3).</param>
/// <param name="RmsJitterSeconds">RMS deviation of the threshold-crossing times from the bit boundary.</param>
/// <param name="OptimalSampleOffsetSeconds">Time offset within the bit period where Q is maximal.</param>
public record EyeMetrics(
    double QFactor,
    double BerEstimate,
    double EyeHeight,
    double EyeWidthSeconds,
    double RmsJitterSeconds,
    double OptimalSampleOffsetSeconds);

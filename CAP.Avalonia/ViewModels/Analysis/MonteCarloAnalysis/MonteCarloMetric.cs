namespace CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;

/// <summary>Which per-run metric the Monte-Carlo analysis evaluates.</summary>
public enum MonteCarloMetric
{
    /// <summary>Insertion-loss spectrum at the output → envelope band over the nominal curve.</summary>
    SpectrumEnvelope,

    /// <summary>Eye-diagram vertical opening (eye height) → distribution histogram.</summary>
    EyeOpenness,
}

/// <summary>ComboBox item pairing a <see cref="MonteCarloMetric"/> with its display label.</summary>
/// <param name="Metric">The metric this option selects.</param>
/// <param name="DisplayName">Human-readable label shown in the dropdown.</param>
public sealed record MonteCarloMetricOption(MonteCarloMetric Metric, string DisplayName)
{
    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

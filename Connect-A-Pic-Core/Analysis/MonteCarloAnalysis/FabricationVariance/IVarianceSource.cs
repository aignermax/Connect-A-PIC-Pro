namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// A source of per-run fabrication variance for Monte-Carlo analysis.
    /// The runner calls <see cref="ApplyVariance"/> before every jittered run and
    /// <see cref="RestoreNominal"/> once at the end (also on cancel or error), so an
    /// implementation must be safe to restore even when no variance was ever applied.
    /// </summary>
    public interface IVarianceSource
    {
        /// <summary>Draws a fresh variance sample from <paramref name="sampler"/> and makes it active.</summary>
        void ApplyVariance(GaussianSampler sampler);

        /// <summary>Deactivates any applied variance so the design simulates nominally again.</summary>
        void RestoreNominal();
    }
}

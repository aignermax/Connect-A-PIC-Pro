namespace CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance
{
    /// <summary>
    /// The sampled cross-section deviation a single component sees in one Monte-Carlo
    /// run: the shared wafer-level process deviation plus a small local
    /// (within-die) term.
    /// </summary>
    /// <param name="DeltaWidthNm">Effective waveguide-width deviation in nm.</param>
    /// <param name="DeltaThicknessNm">Effective core-thickness deviation in nm.</param>
    public sealed record ComponentDeviation(double DeltaWidthNm, double DeltaThicknessNm);
}

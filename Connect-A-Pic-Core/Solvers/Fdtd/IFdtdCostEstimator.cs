namespace CAP_Core.Solvers.Fdtd;

// Capability of an IFdtdSMatrixService whose solves cost money (cloud credits):
// the UI checks for it before submitting so it can show an estimated cost and
// ask for confirmation first. Free backends (Meep in Docker) don't implement it.
public interface IFdtdCostEstimator
{
    Task<FdtdCostEstimate> EstimateCostAsync(FdtdSMatrixRequest request, CancellationToken ct = default);
}

public class FdtdCostEstimate
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public double EstimatedCredits { get; init; }

    public int SimulationCount { get; init; }

    public static FdtdCostEstimate Fail(string error) => new() { Success = false, Error = error };
}

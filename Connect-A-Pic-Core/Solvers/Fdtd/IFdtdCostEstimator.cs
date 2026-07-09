namespace CAP_Core.Solvers.Fdtd;

/// <summary>
/// Optional capability of an <see cref="IFdtdSMatrixService"/> whose solves cost
/// money (cloud credits). The UI checks for this interface before submitting so
/// it can show an estimated cost and ask for confirmation first. Local/free
/// backends (Meep in Docker) simply don't implement it.
/// </summary>
public interface IFdtdCostEstimator
{
    /// <summary>
    /// Estimates the cost of solving <paramref name="request"/> without running
    /// the full simulation. Implementations may briefly contact the cloud
    /// (e.g. upload-and-estimate) but must not consume solve credits.
    /// </summary>
    Task<FdtdCostEstimate> EstimateCostAsync(FdtdSMatrixRequest request, CancellationToken ct = default);
}

/// <summary>
/// Result of a pre-submit cost estimation for a cloud FDTD run.
/// </summary>
public class FdtdCostEstimate
{
    /// <summary>True when an estimate was obtained.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable error when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Estimated total cost in the provider's billing unit (Tidy3D FlexCredits),
    /// covering one simulation per input port.
    /// </summary>
    public double EstimatedCredits { get; init; }

    /// <summary>Number of cloud simulations the solve will submit (one per input port).</summary>
    public int SimulationCount { get; init; }

    /// <summary>Creates a failure estimate.</summary>
    public static FdtdCostEstimate Fail(string error) => new() { Success = false, Error = error };
}

namespace CAP_Core.Routing.MeanderGeneration;

/// <summary>
/// Why meander generation is physically impossible for the given inputs.
/// </summary>
public enum MeanderFailureReason
{
    /// <summary>The target length is shorter than the direct smooth route between the poses.</summary>
    TargetShorterThanDirectPath,

    /// <summary>The bounding rectangle is too small for the required meander at the minimum bend radius.</summary>
    BoundsTooSmallForMeander,

    /// <summary>No tangent-continuous route between the two poses exists at the minimum bend radius.</summary>
    EndpointsNotRoutableAtMinRadius,
}

/// <summary>
/// Result of <see cref="MeanderPathGenerator.Generate"/>: either a valid path whose
/// geometric length is within tolerance of the target, or a typed failure saying why
/// that is impossible. A failure never carries an invalid path.
/// </summary>
public sealed class MeanderResult
{
    private MeanderResult(RoutedPath? path, MeanderFailureReason? failureReason, string? failureMessage)
    {
        Path = path;
        FailureReason = failureReason;
        FailureMessage = failureMessage;
    }

    /// <summary>The generated path; null when generation failed.</summary>
    public RoutedPath? Path { get; }

    /// <summary>Why generation failed; null on success.</summary>
    public MeanderFailureReason? FailureReason { get; }

    /// <summary>Diagnostic detail for the failure; null on success.</summary>
    public string? FailureMessage { get; }

    /// <summary>True when a valid path was generated.</summary>
    public bool IsSuccess => Path != null;

    /// <summary>Creates a successful result.</summary>
    public static MeanderResult Success(RoutedPath path) => new(path, null, null);

    /// <summary>Creates a typed failure result.</summary>
    public static MeanderResult Failure(MeanderFailureReason reason, string message)
        => new(null, reason, message);
}

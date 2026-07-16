using CAP_Core.Components.Connections;

namespace CAP_Core.Analysis;

/// <summary>
/// Type of design issue detected during validation.
/// </summary>
public enum DesignIssueType
{
    /// <summary>
    /// Path violates minimum bend radius constraints (segments too short for bends).
    /// </summary>
    InvalidGeometry,

    /// <summary>
    /// Path could not be routed around obstacles; fallback straight-line path used.
    /// </summary>
    BlockedPath,

    /// <summary>
    /// Two waveguide paths physically overlap, which causes fabrication errors.
    /// This includes regular connections crossing frozen group paths.
    /// </summary>
    OverlappingPaths,

    /// <summary>
    /// A component is placed outside the configured chip boundary.
    /// The component must be moved back into bounds before fabrication.
    /// </summary>
    OutOfBounds,

    /// <summary>
    /// A placed component's PDK no longer matches the design's active fabrication process
    /// (e.g. its process was edited and diverged from the locked process after the component
    /// was placed — issue #570 follow-up). New placements from that PDK are blocked, but the
    /// component itself is kept; this flags it for manual review.
    /// </summary>
    PdkProcessMismatch
}

/// <summary>
/// Represents a single design issue found during validation.
/// Contains the affected connection and location for navigation.
/// </summary>
public class DesignIssue
{
    /// <summary>
    /// The type of issue detected.
    /// </summary>
    public DesignIssueType Type { get; }

    /// <summary>
    /// The affected waveguide connection, if applicable.
    /// Null for issues involving only frozen group paths.
    /// </summary>
    public WaveguideConnection? Connection { get; }

    /// <summary>
    /// X coordinate of the issue midpoint (average of start/end pins) in micrometers.
    /// </summary>
    public double X { get; }

    /// <summary>
    /// Y coordinate of the issue midpoint (average of start/end pins) in micrometers.
    /// </summary>
    public double Y { get; }

    /// <summary>
    /// Human-readable description of the issue.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Creates a new design issue with an associated connection.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <param name="connection">The affected connection (may be null for frozen-path-only overlaps).</param>
    /// <param name="x">Location X in micrometers.</param>
    /// <param name="y">Location Y in micrometers.</param>
    /// <param name="description">Human-readable description.</param>
    public DesignIssue(
        DesignIssueType type,
        WaveguideConnection? connection,
        double x,
        double y,
        string description)
    {
        Type = type;
        Connection = connection;
        X = x;
        Y = y;
        Description = description;
    }
}

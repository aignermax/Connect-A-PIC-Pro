using CAP_Core.Analysis;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Outcome of <see cref="GdsPlacementExecutor.ExecuteAsync"/>: how much of the
/// plan landed on the canvas and why the rest did not. All collections are
/// user-presentable strings, shown in the import dialog's result panel.
/// </summary>
public sealed class GdsPlacementReport
{
    /// <summary>Number of instances placed on the canvas.</summary>
    public int PlacedCount { get; internal set; }

    /// <summary>Number of abutment connections created.</summary>
    public int ConnectedCount { get; internal set; }

    /// <summary>
    /// Number of the created connections that were derived from top-cell route
    /// polygons (subset of <see cref="ConnectedCount"/> — the drawn route told
    /// us the connectivity; the rest are coincident-pin abutments).
    /// </summary>
    public int RouteDerivedCount { get; internal set; }

    /// <summary>Number of top-cell route polygons kept as frozen, non-re-routable paths on the group.</summary>
    public int FrozenRoutePathCount { get; internal set; }

    /// <summary>Number of auto-connected free-pin pairs (experimental pass).</summary>
    public int AutoConnectedCount { get; internal set; }

    /// <summary>Per-pair descriptions of auto-connected free pins.</summary>
    public List<string> AutoConnectedPairs { get; } = new();

    /// <summary>Per-pin reasons for free pins the auto-connect pass did not pair.</summary>
    public List<string> SkippedAutoConnect { get; } = new();

    /// <summary>
    /// Issues the post-batch <see cref="DesignValidator"/> run found in the
    /// connections created by this execution (type, location, involved pins).
    /// </summary>
    public List<string> ValidationWarnings { get; } = new();

    /// <summary>Per-instance reasons for placements that did not happen.</summary>
    public List<string> SkippedPlacements { get; } = new();

    /// <summary>Per-connection reasons for connections that were not created.</summary>
    public List<string> SkippedConnections { get; } = new();

    /// <summary>Non-fatal notes (mirrored instances, non-cardinal rotation snaps).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>True when the placed components were wrapped in a group.</summary>
    public bool GroupCreated { get; internal set; }

    /// <summary>Name of the created group (the imported top cell), or null.</summary>
    public string? GroupName { get; internal set; }
}

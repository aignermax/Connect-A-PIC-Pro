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

    /// <summary>
    /// Number of the created connections that carry a frozen cached route (subset
    /// of <see cref="ConnectedCount"/>) — their geometry came from the import
    /// (traced route polygons, exact abutment straight) and was never re-routed.
    /// </summary>
    public int CachedRouteCount { get; internal set; }

    /// <summary>Number of top-cell route polygons kept as frozen, non-re-routable paths on the group.</summary>
    public int FrozenRoutePathCount { get; internal set; }

    /// <summary>
    /// Number of top-cell non-routing polygons (substrate/base plates, logos)
    /// attached to the group as render-only background geometry.
    /// </summary>
    public int BackgroundPolygonCount { get; internal set; }

    /// <summary>
    /// Number of created connections handed to Lunima's router (one batch
    /// recalculation) instead of keeping imported geometry.
    /// </summary>
    public int ReroutedCount { get; internal set; }

    /// <summary>
    /// Number of connections the opt-in auto-connect stage created between
    /// facing, previously unconnected pins (routed with Lunima's router).
    /// </summary>
    public int AutoConnectedCount { get; internal set; }

    /// <summary>
    /// Number of auto-connected pairs the router could not route — they stay on
    /// the canvas as visible blocked paths and are named in <see cref="Warnings"/>.
    /// </summary>
    public int AutoConnectFailedCount { get; internal set; }

    /// <summary>Number of unconnected pins the auto-connect stage found no facing partner for.</summary>
    public int AutoConnectUnpairedPinCount { get; internal set; }

    /// <summary>
    /// Issues the post-batch <see cref="CAP_Core.Analysis.DesignValidator"/> run found in the
    /// connections created by this execution (type, location, involved pins). Repeated
    /// per-issue lines are grouped per distinct issue (first example + "× N instances").
    /// </summary>
    public List<string> ValidationWarnings { get; } = new();

    /// <summary>Reasons for placements that did not happen — grouped per distinct message (first example + "× N instances").</summary>
    public List<string> SkippedPlacements { get; } = new();

    /// <summary>Per-connection reasons for connections that were not created.</summary>
    public List<string> SkippedConnections { get; } = new();

    /// <summary>Non-fatal notes (per-instance notes grouped per distinct message, non-cardinal rotation snaps).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Chip width (µm) after the import auto-enlarged the playfield to fit the
    /// design, or null when the chip was already big enough. The dialog uses it
    /// to sync the chip-size settings panel.
    /// </summary>
    public double? ChipEnlargedToWidthUm { get; internal set; }

    /// <summary>Chip height (µm) after auto-enlargement, or null (see <see cref="ChipEnlargedToWidthUm"/>).</summary>
    public double? ChipEnlargedToHeightUm { get; internal set; }

    /// <summary>True when the placed components were wrapped in a group.</summary>
    public bool GroupCreated { get; internal set; }

    /// <summary>Name of the created group (the imported top cell), or null.</summary>
    public string? GroupName { get; internal set; }
}

/// <summary>
/// Collapses report lines that repeat per instance into ONE line per distinct
/// message: the first occurrence verbatim, with a " — × N instances" suffix
/// when the message repeated (the <c>GdsImportReporter</c> pattern — first
/// example named, count appended). First-encounter order is kept, so the report
/// stays deterministic. A huge import otherwise floods the dialog and the error
/// console with near-identical per-instance lines.
/// </summary>
internal sealed class GdsReportLineGrouper
{
    private readonly List<string> _keysInEncounterOrder = new();
    private readonly Dictionary<string, (string FirstLine, int Count)> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one occurrence of a message. <paramref name="key"/> is the grouping
    /// identity (the message template, without the per-instance parts);
    /// <paramref name="firstLine"/> is the full line shown for the group (kept from
    /// the first occurrence — it names the first example).
    /// </summary>
    public void Add(string key, string firstLine)
    {
        if (_byKey.TryGetValue(key, out var entry))
        {
            _byKey[key] = (entry.FirstLine, entry.Count + 1);
            return;
        }
        _keysInEncounterOrder.Add(key);
        _byKey[key] = (firstLine, 1);
    }

    /// <summary>Appends the grouped lines to <paramref name="target"/> in first-encounter order.</summary>
    public void FlushInto(List<string> target)
    {
        foreach (var key in _keysInEncounterOrder)
        {
            var (firstLine, count) = _byKey[key];
            target.Add(count == 1 ? firstLine : $"{firstLine} — × {count} instances");
        }
    }
}

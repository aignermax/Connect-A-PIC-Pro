using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Placement instruction for one imported instance, in the application's
/// circuit-space convention (µm, Y-down, origin at the imported layout's
/// top-left). Pure data — no canvas dependencies.
/// </summary>
public sealed record GdsPlacementInstruction
{
    /// <summary>Deterministic instance key (<c>{CellName}#{n}</c>), same as the import's.</summary>
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>
    /// Library template to place: the existing PDK component's identifier for
    /// known cells, the registered component name for imported drafts. Null when
    /// the instance's draft was not registered (see <see cref="Warning"/>).
    /// </summary>
    public string? ComponentIdentifier { get; init; }

    /// <summary>
    /// PDK the template comes from: the known component's PDK, or the import's
    /// user PDK for drafts. Null when unresolved.
    /// </summary>
    public string? PdkSource { get; init; }

    /// <summary>True when the component was registered by this import (draft), false for pre-existing PDK components.</summary>
    public bool IsImportedDraft { get; init; }

    /// <summary>App-space X of the placed bounding box top-left corner, in µm.</summary>
    public double XUm { get; init; }

    /// <summary>App-space Y of the placed bounding box top-left corner, in µm.</summary>
    public double YUm { get; init; }

    /// <summary>
    /// Placement rotation in cardinal degrees, in the <c>Component.RotationDegrees</c>
    /// convention (pin world angle = local angle + RotationDegrees) — assign verbatim.
    /// </summary>
    public double RotationDegrees { get; init; }

    /// <summary>True when the GDS reference was mirrored; the core model cannot mirror, so the component is placed unreflected.</summary>
    public bool Reflected { get; init; }

    /// <summary>User-presentable note for this instance (mirroring, unregistered draft), or null.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// One reconstructed abutment connection between two pins. An endpoint with
/// <see cref="GdsConnectionEndpoint.IsTopLevelPort"/> set is an external port
/// of the imported circuit — v1 leaves it free (no canvas connection).
/// </summary>
public sealed record GdsConnectionEndpoint
{
    /// <summary>Index into <see cref="GdsPlacementPlan.Placements"/>, or −1 for a top-cell port.</summary>
    public int InstanceIndex { get; init; } = -1;

    /// <summary>Pin name on that instance's component (or the top-cell port name).</summary>
    public string PinName { get; init; } = string.Empty;

    /// <summary>True when this endpoint is an external port of the imported circuit (leave free in v1).</summary>
    public bool IsTopLevelPort => InstanceIndex < 0;
}

/// <summary>A reconstructed connection between two instance pins (or an instance pin and a top-cell port).</summary>
public sealed record GdsConnectionInstruction
{
    /// <summary>First endpoint.</summary>
    public GdsConnectionEndpoint A { get; init; } = new();

    /// <summary>Second endpoint.</summary>
    public GdsConnectionEndpoint B { get; init; } = new();

    /// <summary>App-space X of the connection point in µm (informational).</summary>
    public double XUm { get; init; }

    /// <summary>App-space Y of the connection point in µm (informational).</summary>
    public double YUm { get; init; }

    /// <summary>True when either endpoint is a top-cell port — v1 skips these connections.</summary>
    public bool InvolvesTopLevelPort => A.IsTopLevelPort || B.IsTopLevelPort;

    /// <summary>User-presentable note, set for skipped top-cell-port connections.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Data-only placement plan for a completed GDS import: an ordered list of
/// placement instructions plus the reconstructed connections. All placements
/// belong to one group named after the top cell. Produced by
/// <see cref="FromOutcome"/> — a pure function, no canvas dependencies; the UI
/// layer resolves each <see cref="GdsPlacementInstruction.ComponentIdentifier"/>
/// to a library template and executes the plan.
/// </summary>
public sealed record GdsPlacementPlan
{
    /// <summary>Name of the single group all placements belong to (the imported top cell).</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>Placement instructions in GDS placement order (indexes align with connection endpoints).</summary>
    public IReadOnlyList<GdsPlacementInstruction> Placements { get; init; } =
        Array.Empty<GdsPlacementInstruction>();

    /// <summary>Reconstructed connections; top-cell-port endpoints are flagged, not dropped.</summary>
    public IReadOnlyList<GdsConnectionInstruction> Connections { get; init; } =
        Array.Empty<GdsConnectionInstruction>();

    /// <summary>The import's warnings, carried along for display.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Builds the plan for an import outcome. Pure; never throws on missing data.</summary>
    public static GdsPlacementPlan FromOutcome(GdsImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var registeredByDraftName = outcome.RegisteredComponents
            .ToDictionary(r => r.CellDraftName, r => r.ComponentName, StringComparer.Ordinal);

        var placements = outcome.Instances.Select(instance =>
        {
            string? identifier;
            string? pdkSource;
            string? warning = null;
            var isDraft = instance.CellDraftName is not null;

            if (isDraft)
            {
                if (registeredByDraftName.TryGetValue(instance.CellDraftName!, out var registeredName))
                {
                    identifier = registeredName;
                    pdkSource = outcome.UserPdkName;
                }
                else
                {
                    identifier = null;
                    pdkSource = null;
                    warning = $"Cell '{instance.CellDraftName}' was not registered; this instance cannot be placed.";
                }
            }
            else
            {
                identifier = instance.KnownComponentIdentifier;
                pdkSource = instance.PdkSource;
            }

            if (instance.Reflected)
            {
                var mirrorNote = "mirrored in GDS (STRANS) — placed unreflected (v1 limitation)";
                warning = warning is null ? mirrorNote : $"{warning} Also: {mirrorNote}.";
            }

            return new GdsPlacementInstruction
            {
                InstanceName = instance.InstanceName,
                ComponentIdentifier = identifier,
                PdkSource = pdkSource,
                IsImportedDraft = isDraft,
                XUm = instance.PositionXUm,
                YUm = instance.PositionYUm,
                RotationDegrees = instance.RotationDegrees,
                Reflected = instance.Reflected,
                Warning = warning,
            };
        }).ToList();

        var connections = outcome.Connections.Select(pair => new GdsConnectionInstruction
        {
            A = MapEndpoint(pair.A),
            B = MapEndpoint(pair.B),
            XUm = pair.XUm,
            YUm = pair.YUm,
            Note = pair.A.IsTopLevelPort || pair.B.IsTopLevelPort
                ? "involves a top-cell port of the imported circuit — left free in v1"
                : null,
        }).ToList();

        return new GdsPlacementPlan
        {
            GroupName = outcome.TopCellName,
            Placements = placements,
            Connections = connections,
            Warnings = outcome.Warnings,
        };
    }

    private static GdsConnectionEndpoint MapEndpoint(GdsPinEndpoint endpoint) => new()
    {
        InstanceIndex = endpoint.InstanceIndex,
        PinName = endpoint.PinName,
    };
}

using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Executes a <see cref="GdsPlacementPlan"/> on the design canvas: places every
/// instance at its exact GDS-derived position (no placement-search nudging, which
/// would break abutment), reconstructs the abutment connections, optionally
/// auto-connects still-free optical pins that face each other (experimental),
/// validates the created connections, and wraps the placed components in a group
/// named after the imported top cell. Non-UI and headless-testable — the canvas
/// ViewModel works without a window.
/// <para>
/// Placement and grouping go through the undo stack (<see cref="PlaceComponentCommand.CreateExact"/>
/// and <see cref="CreateGroupCommand"/>) when a <see cref="CommandManager"/> is
/// supplied; pin connections follow the app's programmatic-connect path
/// (<see cref="DesignCanvasViewModel.ConnectPinsAsync"/>), which — like interactive
/// pin-drag connects — is not individually undoable.
/// </para>
/// </summary>
public sealed class GdsPlacementExecutor
{
    /// <summary>Default search radius (µm) for the experimental auto-connect pass.</summary>
    public const double DefaultAutoConnectRadiusUm = 1000.0;

    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager? _commandManager;
    private readonly Func<IReadOnlyList<ComponentTemplate>> _templateProvider;

    /// <summary>Initializes a new <see cref="GdsPlacementExecutor"/>.</summary>
    /// <param name="canvas">Canvas the imported circuit is placed onto.</param>
    /// <param name="commandManager">
    /// Undo stack for placement/group commands; null executes without undo support
    /// (e.g. headless runs).
    /// </param>
    /// <param name="templateProvider">
    /// Supplies the currently loaded component templates for resolving
    /// <see cref="GdsPlacementInstruction.ComponentIdentifier"/> (e.g.
    /// <c>() => leftPanel.AllTemplates.ToList()</c>).
    /// </param>
    public GdsPlacementExecutor(
        DesignCanvasViewModel canvas,
        CommandManager? commandManager,
        Func<IReadOnlyList<ComponentTemplate>> templateProvider)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _commandManager = commandManager;
        _templateProvider = templateProvider ?? throw new ArgumentNullException(nameof(templateProvider));
    }

    /// <summary>
    /// Executes <paramref name="plan"/> in placement order: place+rotate all
    /// instances first, then connect, then (optionally) auto-connect free pins,
    /// then validate, then group (grouping freezes internal connections, so it
    /// must run last).
    /// </summary>
    /// <param name="plan">The placement plan built from an import outcome.</param>
    /// <param name="progress">Optional user-presentable stage reporter.</param>
    /// <param name="ct">
    /// Cancellation token. Cancellation between steps leaves the already-placed
    /// components on the canvas (undoable via the command history).
    /// </param>
    /// <param name="autoConnectFreePins">
    /// Experimental: after the abutment connections, pair still-unoccupied optical
    /// pins whose absolute angles oppose each other (see <see cref="GdsFreePinPairer"/>)
    /// and connect each pair. Every pair and every skipped free pin (with reason)
    /// lands in the report.
    /// </param>
    /// <param name="autoConnectRadiusUm">
    /// Maximum pin-to-pin distance (µm) for an auto-connected pair.
    /// </param>
    /// <returns>A report of what was placed, connected, and skipped.</returns>
    public async Task<GdsPlacementReport> ExecuteAsync(
        GdsPlacementPlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool autoConnectFreePins = false,
        double autoConnectRadiusUm = DefaultAutoConnectRadiusUm)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = new GdsPlacementReport();
        var templates = _templateProvider();

        var placedViewModels = PlaceAll(plan, templates, report, progress, ct);
        // No ConfigureAwait(false) here: continuations mutate the canvas'
        // ObservableCollections and must stay on the caller's (UI) context.
        var createdConnections = await ConnectAllAsync(plan, placedViewModels, report, progress, ct);
        if (autoConnectFreePins)
        {
            await AutoConnectFreePinsAsync(
                plan, placedViewModels, createdConnections, report, progress, autoConnectRadiusUm, ct);
        }
        ValidateCreatedConnections(createdConnections, report);
        CreateGroup(plan, placedViewModels, report, progress, ct);
        return report;
    }

    // ── Stages ───────────────────────────────────────────────────────────────

    private List<ComponentViewModel?> PlaceAll(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentTemplate> templates,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Index-aligned with plan.Placements; null entries mark skipped instances
        // so connection endpoint indexes stay valid.
        var placedViewModels = new List<ComponentViewModel?>(plan.Placements.Count);

        for (var i = 0; i < plan.Placements.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var instruction = plan.Placements[i];
            progress?.Report($"Placing component {i + 1}/{plan.Placements.Count} ({instruction.InstanceName})…");

            if (instruction.ComponentIdentifier is null)
            {
                report.SkippedPlacements.Add(
                    $"'{instruction.InstanceName}': {instruction.Warning ?? "no component to place."}");
                placedViewModels.Add(null);
                continue;
            }

            var template = templates.FirstOrDefault(t =>
                t.Name == instruction.ComponentIdentifier && t.PdkSource == instruction.PdkSource);
            if (template is null)
            {
                report.SkippedPlacements.Add(
                    $"'{instruction.InstanceName}': template '{instruction.ComponentIdentifier}' " +
                    $"from PDK '{instruction.PdkSource}' is not in the library.");
                placedViewModels.Add(null);
                continue;
            }

            var quarterTurns = SnapToQuarterTurns(instruction.RotationDegrees, instruction.InstanceName, report);
            var command = PlaceComponentCommand.CreateExact(
                _canvas, template, instruction.XUm, instruction.YUm, quarterTurns);
            Execute(command);

            placedViewModels.Add(command.CreatedViewModel);
            report.PlacedCount++;
            if (instruction.Warning is not null)
                report.Warnings.Add($"'{instruction.InstanceName}': {instruction.Warning}");
        }

        return placedViewModels;
    }

    /// <summary>
    /// Recreates the plan's abutment connections; returns the connections actually
    /// created so the validation stage can check exactly this execution's additions.
    /// </summary>
    private async Task<List<WaveguideConnection>> ConnectAllAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var created = new List<WaveguideConnection>();
        for (var i = 0; i < plan.Connections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var connection = plan.Connections[i];
            progress?.Report($"Connecting pins {i + 1}/{plan.Connections.Count}…");

            if (connection.InvolvesTopLevelPort)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: {connection.Note ?? "involves a top-cell port."}");
                continue;
            }

            var startVm = placedViewModels[connection.A.InstanceIndex];
            var endVm = placedViewModels[connection.B.InstanceIndex];
            if (startVm is null || endVm is null)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: an endpoint instance was not placed.");
                continue;
            }

            var startPin = startVm.Component.PhysicalPins.FirstOrDefault(p => p.Name == connection.A.PinName);
            var endPin = endVm.Component.PhysicalPins.FirstOrDefault(p => p.Name == connection.B.PinName);
            if (startPin is null || endPin is null)
            {
                report.SkippedConnections.Add(
                    $"{Describe(connection)}: pin not found on the placed component.");
                continue;
            }

            var connectionVm = await _canvas.ConnectPinsAsync(startPin, endPin);
            if (connectionVm is not null)
                created.Add(connectionVm.Connection);
            report.ConnectedCount++;
        }
        return created;
    }

    /// <summary>
    /// Experimental auto-connect (issue #808 follow-up): pairs the still-unoccupied
    /// optical pins of the placed instances via <see cref="GdsFreePinPairer"/> and
    /// connects each pair. Occupancy is checked against the canvas connection state
    /// (<see cref="DesignCanvasViewModel.GetConnectionForPin"/>) because
    /// <c>ConnectPinsAsync</c> REPLACES existing pin connections — only pins with no
    /// connection at all are candidates. All pairs are added deferred
    /// (<see cref="DesignCanvasViewModel.ConnectPins"/>) and routed in ONE
    /// recalculation at the end instead of a per-pair re-route storm.
    /// </summary>
    private async Task AutoConnectFreePinsAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        List<WaveguideConnection> createdConnections,
        GdsPlacementReport report,
        IProgress<string>? progress,
        double radiusUm,
        CancellationToken ct)
    {
        progress?.Report("Auto-connecting free pins…");

        var candidates = new List<GdsFreePinCandidate>();
        var candidatePins = new List<PhysicalPin>();
        for (var i = 0; i < placedViewModels.Count; i++)
        {
            var vm = placedViewModels[i];
            if (vm is null) continue;

            // Ordinal pin-name order keeps the pairing deterministic across runs.
            foreach (var pin in vm.Component.PhysicalPins.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var label = $"'{plan.Placements[i].InstanceName}.{pin.Name}'";
                if (PinKindHelper.IsElectrical(pin))
                {
                    report.SkippedAutoConnect.Add($"{label}: non-optical (electrical) pin.");
                    continue;
                }
                if (_canvas.GetConnectionForPin(pin) is not null)
                    continue; // occupied by an abutment connection — not a free pin.

                var (x, y) = pin.GetAbsolutePosition();
                candidates.Add(new GdsFreePinCandidate(label, x, y, pin.GetAbsoluteAngle(), i));
                candidatePins.Add(pin);
            }
        }

        var pairing = GdsFreePinPairer.Pair(candidates, radiusUm);

        foreach (var skip in pairing.Skipped)
            report.SkippedAutoConnect.Add(FormatAutoConnectSkip(candidates[skip.Index].Label, skip, radiusUm));

        foreach (var pair in pairing.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            var connectionVm = _canvas.ConnectPins(candidatePins[pair.A], candidatePins[pair.B]);
            if (connectionVm is not null)
                createdConnections.Add(connectionVm.Connection);
            report.AutoConnectedCount++;
            report.AutoConnectedPairs.Add(string.Create(CultureInfo.InvariantCulture,
                $"{candidates[pair.A].Label} ↔ {candidates[pair.B].Label} ({pair.DistanceUm:0.#} µm)"));
        }

        if (pairing.Pairs.Count > 0)
            await _canvas.RecalculateRoutesAsync(); // one routing pass for the whole batch
    }

    /// <summary>
    /// Post-batch honesty net: runs <see cref="DesignValidator"/> over exactly the
    /// connections created by this execution (abutment + auto-connect), including
    /// overlap checks against the frozen paths of groups already on the canvas,
    /// and appends the issues to the report as validation warnings.
    /// Coincident-pin connections (a perfect GDS abutment: the pins sit at the
    /// same point, below the router's own 1 µm endpoint tolerance) have NO routed
    /// geometry to validate — the CSC fallback still flags their degenerate route
    /// as blocked, so including them would plaster every standard abutment import
    /// with false BlockedPath warnings. They are filtered out here instead.
    /// </summary>
    private void ValidateCreatedConnections(
        IReadOnlyList<WaveguideConnection> createdConnections,
        GdsPlacementReport report)
    {
        var routable = createdConnections
            .Where(c => PinDistanceUm(c) >= DegenerateRouteThresholdUm)
            .ToList();
        if (routable.Count == 0)
            return;

        var existingGroups = _canvas.Components
            .Select(c => c.Component)
            .OfType<ComponentGroup>()
            .ToList();
        var issues = new DesignValidator().Validate(routable, existingGroups);
        foreach (var issue in issues)
        {
            report.ValidationWarnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{issue.Type} at ({issue.X:0.#}, {issue.Y:0.#}) µm — {issue.Description}"));
        }
    }

    /// <summary>
    /// Pin-to-pin distance (µm) below which a route is degenerate — aligned with
    /// the router's endpoint tolerance (a route this short is a perfect abutment,
    /// not a waveguide).
    /// </summary>
    private const double DegenerateRouteThresholdUm = 1.0;

    private static double PinDistanceUm(WaveguideConnection connection)
    {
        var (x1, y1) = connection.StartPin.GetAbsolutePosition();
        var (x2, y2) = connection.EndPin.GetAbsolutePosition();
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string FormatAutoConnectSkip(string label, GdsFreePinSkip skip, double radiusUm) =>
        skip.Reason switch
        {
            GdsFreePinSkipReason.AmbiguousNearestPartner => string.Create(CultureInfo.InvariantCulture,
                $"{label}: ambiguous — two opposing candidates at nearly the same distance " +
                $"({skip.NearestDistanceUm:0.#} vs {skip.SecondNearestDistanceUm:0.#} µm)."),
            _ => string.Create(CultureInfo.InvariantCulture,
                $"{label}: no opposing free pin within {radiusUm:0.#} µm."),
        };

    private void CreateGroup(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var groupCandidates = placedViewModels.OfType<ComponentViewModel>().ToList();
        if (groupCandidates.Count < 2)
            return; // CreateGroupCommand needs ≥2 components; a lone component stays ungrouped.

        progress?.Report($"Grouping {groupCandidates.Count} components as '{plan.GroupName}'…");
        var command = new CreateGroupCommand(_canvas, groupCandidates);
        Execute(command);

        if (command.CreatedGroup is null)
            return; // grouping was rejected (e.g. locked components) — components stay ungrouped.

        command.CreatedGroup.GroupName = plan.GroupName;
        report.GroupCreated = true;
        report.GroupName = plan.GroupName;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Execute(IUndoableCommand command)
    {
        if (_commandManager is not null)
            _commandManager.ExecuteCommand(command);
        else
            command.Execute();
    }

    /// <summary>
    /// The plan contract guarantees cardinal rotations; a non-cardinal value is
    /// snapped to the nearest quarter turn and surfaced as a warning instead of
    /// silently misplacing the instance.
    /// </summary>
    private static int SnapToQuarterTurns(double rotationDegrees, string instanceName, GdsPlacementReport report)
    {
        var snappedTurns = (int)Math.Round(rotationDegrees / 90.0);
        var snappedDegrees = snappedTurns * 90.0;
        if (Math.Abs(rotationDegrees - snappedDegrees) > 0.001)
        {
            report.Warnings.Add(
                $"'{instanceName}': non-cardinal rotation {rotationDegrees:0.###}° snapped to {snappedDegrees % 360:0}°.");
        }
        return ((snappedTurns % 4) + 4) % 4;
    }

    private static string Describe(GdsConnectionInstruction connection) =>
        $"connection #{connection.A.InstanceIndex}:{connection.A.PinName} ↔ #{connection.B.InstanceIndex}:{connection.B.PinName}";
}

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

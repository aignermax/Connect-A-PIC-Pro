using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Executes a <see cref="GdsPlacementPlan"/> on the design canvas: places every
/// instance at its exact GDS-derived position (no placement-search nudging, which
/// would break abutment), reconstructs the abutment connections, and wraps the
/// placed components in a group named after the imported top cell. Non-UI and
/// headless-testable — the canvas ViewModel works without a window.
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
    /// instances first, then connect, then group (grouping freezes internal
    /// connections, so it must run last).
    /// </summary>
    /// <param name="plan">The placement plan built from an import outcome.</param>
    /// <param name="progress">Optional user-presentable stage reporter.</param>
    /// <param name="ct">
    /// Cancellation token. Cancellation between steps leaves the already-placed
    /// components on the canvas (undoable via the command history).
    /// </param>
    /// <returns>A report of what was placed, connected, and skipped.</returns>
    public async Task<GdsPlacementReport> ExecuteAsync(
        GdsPlacementPlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = new GdsPlacementReport();
        var templates = _templateProvider();

        var placedViewModels = PlaceAll(plan, templates, report, progress, ct);
        // No ConfigureAwait(false) here: continuations mutate the canvas'
        // ObservableCollections and must stay on the caller's (UI) context.
        await ConnectAllAsync(plan, placedViewModels, report, progress, ct);
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

    private async Task ConnectAllAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct)
    {
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

            await _canvas.ConnectPinsAsync(startPin, endPin);
            report.ConnectedCount++;
        }
    }

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

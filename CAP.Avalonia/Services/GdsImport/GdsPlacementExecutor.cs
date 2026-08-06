using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;

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
/// When the canvas already holds content, the whole import is shifted by ONE
/// uniform translation first (<see cref="ComputeImportOriginOffset"/>): the
/// import's bounding box starts right of the existing content with a margin, so
/// the import never stacks on top of the existing design. The internal relative
/// geometry stays exact — abutment is preserved. An empty canvas keeps the raw
/// GDS coordinates (no offset).
/// </para>
/// <para>
/// Placement and grouping go through the undo stack (<see cref="PlaceComponentCommand.CreateExact"/>
/// and <see cref="CreateGroupCommand"/>) when a <see cref="CommandManager"/> is
/// supplied; pin connections keep the geometry the import recovered (drawn route
/// polygons, or the exact pin-to-pin straight of a coincident abutment) as frozen
/// cached routes — the same hardcoded-path mechanism .lun loading uses
/// (<see cref="DesignCanvasViewModel.ConnectPinsWithCachedRoute"/>) — and only
/// connections WITHOUT recovered geometry are routed in one deferred recalculation
/// per stage instead of a per-connection re-route — like interactive
/// pin-drag connects, they are not individually undoable.
/// </para>
/// </summary>
public sealed partial class GdsPlacementExecutor
{
    /// <summary>
    /// Default search radius (µm) for the experimental auto-connect pass.
    /// Deliberately short: a circuit-spanning default would wire far-apart
    /// external ports together.
    /// </summary>
    public const double DefaultAutoConnectRadiusUm = 200.0;

    /// <summary>
    /// Horizontal gap (µm) between the existing canvas content's right edge and an
    /// import's bounding box when the canvas is not empty (see
    /// <see cref="ComputeImportOriginOffset"/>).
    /// </summary>
    public const double ExistingContentMarginUm = 50.0;

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
    /// Instances placed by the in-flight (or most recent) <see cref="ExecuteAsync"/>
    /// run. Read after a cancellation to tell the user how much of the import
    /// already landed on the canvas — and must be undone or deleted before
    /// re-importing, or the next run stacks a second copy on top.
    /// </summary>
    public int PlacedCountSoFar { get; private set; }

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
    /// pins whose absolute angles oppose each other AND that face each other
    /// (see <see cref="GdsFreePinPairer"/>) and connect each pair. Every pair and
    /// every skipped free pin (with reason) lands in the report.
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
        PlacedCountSoFar = 0;

        // Computed BEFORE anything is placed: the canvas must still hold only the
        // pre-import content for the free-space rule.
        var originOffset = ComputeImportOriginOffset(plan);
        var placedViewModels = await PlaceAllAsync(plan, templates, report, progress, ct, originOffset);
        if (originOffset != (0.0, 0.0) && report.PlacedCount > 0)
        {
            // Only a placement that actually happened may claim the shift — a plan
            // whose templates all went missing placed nothing at the offset.
            report.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.PlacedNextToExistingContentFormat"),
                originOffset.X));
        }
        // No ConfigureAwait(false) here: continuations mutate the canvas'
        // ObservableCollections and must stay on the caller's (UI) context.
        var createdConnections = await ConnectAllAsync(plan, placedViewModels, report, progress, ct, originOffset);
        if (autoConnectFreePins)
        {
            await AutoConnectFreePinsAsync(
                plan, placedViewModels, createdConnections, report, progress, autoConnectRadiusUm, ct);
        }
        ValidateCreatedConnections(createdConnections, report);
        CreateGroup(plan, placedViewModels, report, progress, ct, originOffset);
        return report;
    }

    // ── Stages ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How often the placement/connect loops yield to the UI message loop. The
    /// canvas must mutate on the UI thread, but an all-cached import no longer
    /// hits an await between the stages — without a periodic yield the dialog
    /// freezes for the whole placement (progress reports queue up unrendered
    /// and Cancel stays unclickable). Yielding posts the continuation back to
    /// the same (UI) context: the thread rule is untouched.
    /// </summary>
    private const int UiYieldInterval = 64;

    /// <summary>
    /// Minimum interval between throttled stage-progress messages of a run
    /// (<see cref="GdsStageProgressReporter"/>): per-item reports post to the UI
    /// dispatcher, so huge imports would flood the message loop without the
    /// throttle. Internal set-seam for tests (zero reports every item).
    /// </summary>
    internal TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Creates a throttled reporter for a stage loop, or null when no progress sink exists.</summary>
    private GdsStageProgressReporter? StageProgress(IProgress<string>? progress, string stageName) =>
        progress is null ? null : new GdsStageProgressReporter(progress, stageName, ProgressReportInterval);

    private async Task<List<ComponentViewModel?>> PlaceAllAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentTemplate> templates,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct,
        (double X, double Y) originOffset)
    {
        // Index-aligned with plan.Placements; null entries mark skipped instances
        // so connection endpoint indexes stay valid.
        var placedViewModels = new List<ComponentViewModel?>(plan.Placements.Count);
        var stageProgress = StageProgress(progress, "Placing components");

        for (var i = 0; i < plan.Placements.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % UiYieldInterval == UiYieldInterval - 1)
                await Task.Yield();
            var instruction = plan.Placements[i];
            stageProgress?.Report(i + 1, plan.Placements.Count);

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
                _canvas, template, instruction.XUm + originOffset.X, instruction.YUm + originOffset.Y, quarterTurns,
                mirrorPinsHorizontally: instruction.Reflected);
            Execute(command);

            placedViewModels.Add(command.CreatedViewModel);
            report.PlacedCount++;
            PlacedCountSoFar++;
            if (instruction.Warning is not null)
                report.Warnings.Add($"'{instruction.InstanceName}': {instruction.Warning}");
        }

        return placedViewModels;
    }

    private void CreateGroup(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct,
        (double X, double Y) originOffset)
    {
        ct.ThrowIfCancellationRequested();
        var groupCandidates = placedViewModels.OfType<ComponentViewModel>().ToList();
        if (groupCandidates.Count < 2)
        {
            WarnOnDroppedRouteGeometry(plan, report);
            return; // CreateGroupCommand needs ≥2 components; a lone component stays ungrouped.
        }

        progress?.Report($"Grouping {groupCandidates.Count} components as '{plan.GroupName}'…");
        // The final name rides into the command: the group is named when it is
        // constructed — BEFORE the group ViewModel is added and selected. Renaming
        // after selection would leave bound panels showing the placeholder
        // Group_HHmmss name (ComponentViewModel.DisplayName has no change
        // notification).
        var command = new CreateGroupCommand(_canvas, groupCandidates, plan.GroupName);
        Execute(command);

        if (command.CreatedGroup is null)
        {
            WarnOnDroppedRouteGeometry(plan, report);
            return; // grouping was rejected (e.g. locked components) — components stay ungrouped.
        }

        report.GroupCreated = true;
        report.GroupName = plan.GroupName;
        report.FrozenRoutePathCount = plan.TopCellWaveguidePolygons.Count;

        // The top cell's own routing geometry (waveguide-layer polygons) becomes
        // pin-less frozen paths on the group: visible and persistent, but not
        // re-routable. Polygons that bridged exactly two pins already became
        // real connections upstream (route derivation) and are not in this list.
        // The polygons are in plan space, so the import origin offset the
        // placements already received must be applied here too — frozen paths
        // hold absolute canvas coordinates.
        var frozenProgress = StageProgress(progress, "Attaching frozen route paths");
        for (var i = 0; i < plan.TopCellWaveguidePolygons.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            frozenProgress?.Report(i + 1, plan.TopCellWaveguidePolygons.Count);
            command.CreatedGroup.AddInternalPath(
                GdsFrozenRoutePathFactory.Create(plan.TopCellWaveguidePolygons[i], originOffset.X, originOffset.Y));
        }
    }

    /// <summary>
    /// The import warning promises the top cell's routing geometry comes back as
    /// frozen paths on the group — when no group was created there is nothing to
    /// attach them to, and the geometry would vanish silently without this note.
    /// </summary>
    private static void WarnOnDroppedRouteGeometry(GdsPlacementPlan plan, GdsPlacementReport report)
    {
        if (plan.TopCellWaveguidePolygons.Count > 0)
        {
            report.Warnings.Add(
                $"Top-cell routing geometry ({plan.TopCellWaveguidePolygons.Count} waveguide " +
                "polygon(s)) was not imported: no group was created to hold the frozen paths.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The uniform translation the whole import receives so it lands in free space:
    /// when the canvas already holds components, the import's bounding-box top-left
    /// moves to (existing maxX + <see cref="ExistingContentMarginUm"/>, existing minY)
    /// — right of the existing content, top-aligned with it. Every placement shifts
    /// by the same delta, so the import's internal relative geometry (abutment!)
    /// stays exact. Returns (0, 0) — keep the raw GDS coordinates — on an empty
    /// canvas or when the plan has nothing placeable.
    /// </summary>
    private (double X, double Y) ComputeImportOriginOffset(GdsPlacementPlan plan)
    {
        var existing = BoundingBoxCalculator.Calculate(_canvas.Components);
        if (existing is null)
            return (0.0, 0.0);

        var importMinX = double.MaxValue;
        var importMinY = double.MaxValue;
        var anyPlaceable = false;
        foreach (var placement in plan.Placements)
        {
            if (placement.ComponentIdentifier is null)
                continue; // unplaceable instances never land on the canvas — exclude from the bbox.
            anyPlaceable = true;
            importMinX = Math.Min(importMinX, placement.XUm);
            importMinY = Math.Min(importMinY, placement.YUm);
        }
        if (!anyPlaceable)
            return (0.0, 0.0);

        return (existing.Value.MaxX + ExistingContentMarginUm - importMinX,
                existing.Value.MinY - importMinY);
    }

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
    /// silently misplacing the instance. Exact midpoints (x.5 turns, e.g. 45°)
    /// round AWAY FROM ZERO — <see cref="Math.Round(double)"/>'s banker's
    /// rounding would snap 45° down to 0° but 135° up to 180°.
    /// </summary>
    private static int SnapToQuarterTurns(double rotationDegrees, string instanceName, GdsPlacementReport report)
    {
        var snappedTurns = (int)Math.Round(rotationDegrees / 90.0, MidpointRounding.AwayFromZero);
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

using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Executes a <see cref="GdsPlacementPlan"/> on the design canvas: places every
/// instance at its exact GDS-derived position (no placement-search nudging, which
/// would break abutment), reconstructs the detected connections (frozen imported
/// geometry, or re-routed with Lunima's own router on request), validates the
/// created connections, and wraps the placed components in a group named after
/// the imported top cell. Non-UI and headless-testable — the canvas ViewModel
/// works without a window.
/// <para>
/// When the canvas already holds content, the whole import is shifted by ONE
/// uniform translation first (<see cref="ComputeImportOriginOffset"/>): the
/// import's bounding box starts right of the existing content with a margin, so
/// the import never stacks on top of the existing design. The internal relative
/// geometry stays exact — abutment is preserved. An empty canvas keeps the raw
/// GDS coordinates, except that negative origins shift to the chip origin. A
/// design bigger than the chip enlarges the playfield and routing grid
/// (<see cref="EnsureDesignFitsChip"/>).
/// </para>
/// <para>
/// Placement and grouping go through the undo stack (<see cref="PlaceComponentCommand.CreateExact"/>
/// and <see cref="CreateGroupCommand"/>) when a <see cref="CommandManager"/> is
/// supplied. Pin connections either keep the geometry the import recovered
/// (drawn route polygons, or the exact pin-to-pin straight of a coincident
/// abutment) as frozen cached routes — the same hardcoded-path mechanism .lun
/// loading uses (<see cref="DesignCanvasViewModel.ConnectPinsWithCachedRoute"/>) —
/// or, when re-routing is requested, are handed to Lunima's own router in one
/// deferred recalculation per stage instead of a per-connection re-route. Like
/// interactive pin-drag connects, they are not individually undoable.
/// </para>
/// </summary>
public sealed partial class GdsPlacementExecutor
{
    /// <summary>
    /// Upper bound on the number of route-derived connections handed to the live
    /// router in one import. Above it the import silently keeps the frozen
    /// imported geometry instead (with a report warning): a failed incremental
    /// route triggers full re-route attempts in several orderings, which at
    /// thousands of connections turns the import into a minutes-long hang.
    /// </summary>
    public const int MaxReroutedConnections = 300;

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
    /// instances first, then connect, then validate, then group (grouping
    /// freezes internal connections, so it must run last).
    /// </summary>
    /// <param name="plan">The placement plan built from an import outcome.</param>
    /// <param name="progress">Optional user-presentable stage reporter.</param>
    /// <param name="ct">
    /// Cancellation token. Cancellation between steps leaves the already-placed
    /// components on the canvas (undoable via the command history).
    /// </param>
    /// <param name="rerouteImportedConnections">
    /// True (the default) re-creates the route-derived connections with Lunima's
    /// own router — real waveguides and metal traces instead of the imported
    /// polygon geometry — capped at <see cref="MaxReroutedConnections"/> (above
    /// the cap the import keeps the frozen geometry and says so in the report).
    /// False keeps every recovered geometry as a frozen cached route.
    /// Coincident-pin abutments stay frozen either way: their zero-length
    /// straight IS the honest route, and the router's degenerate fallback would
    /// only flag them blocked.
    /// </param>
    /// <returns>A report of what was placed, connected, and skipped.</returns>
    public async Task<GdsPlacementReport> ExecuteAsync(
        GdsPlacementPlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool rerouteImportedConnections = true)
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
        // Before any routing: a design larger than the chip would smear its
        // obstacles along the clamped A* grid border and fail every route.
        EnsureDesignFitsChip(report);
        // No ConfigureAwait(false) here: continuations mutate the canvas'
        // ObservableCollections and must stay on the caller's (UI) context.
        var createdConnections = await ConnectAllAsync(
            plan, placedViewModels, report, progress, ct, originOffset, rerouteImportedConnections);
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
        // First-wins per key, matching what a linear FirstOrDefault scan returned
        // — a per-placement scan makes a 5000-instance import O(N×T).
        var templatesByKey = new Dictionary<(string Name, string? PdkSource), ComponentTemplate>();
        foreach (var template in templates)
            templatesByKey.TryAdd((template.Name, template.PdkSource), template);
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

            templatesByKey.TryGetValue(
                (instruction.ComponentIdentifier, instruction.PdkSource), out var template);
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

        AttachBackgroundGeometry(plan, command.CreatedGroup, report, originOffset);
    }

    /// <summary>
    /// Attaches the top cell's non-routing polygons (substrate/base plates,
    /// exclusion zones, logos) to the group as render-only outline geometry.
    /// Deliberately NOT frozen paths or a component: both register routing
    /// obstacles, and a base plate spanning the whole design would wall off
    /// every route. Outline points are stored relative to the group's top-left,
    /// like any component's outline polygons.
    /// </summary>
    private static void AttachBackgroundGeometry(
        GdsPlacementPlan plan,
        CAP_Core.Components.Core.ComponentGroup group,
        GdsPlacementReport report,
        (double X, double Y) originOffset)
    {
        if (plan.TopCellResidualPolygons.Count == 0)
            return;

        var offsetX = originOffset.X - group.PhysicalX;
        var offsetY = originOffset.Y - group.PhysicalY;
        group.OutlinePolygons = plan.TopCellResidualPolygons
            .Select(p => new CAP_Core.Components.Core.OutlinePolygon
            {
                Layer = p.Layer,
                DataType = p.DataType,
                Points = p.Points
                    .Select(pt => new CAP_Core.Components.Core.OutlinePoint(pt.X + offsetX, pt.Y + offsetY))
                    .ToList(),
            })
            .ToList();
        report.BackgroundPolygonCount = plan.TopCellResidualPolygons.Count;
    }

    /// <summary>
    /// The import warning promises the top cell's routing and background geometry
    /// comes back on the group — when no group was created there is nothing to
    /// attach it to, and the geometry would vanish silently without this note.
    /// </summary>
    private static void WarnOnDroppedRouteGeometry(GdsPlacementPlan plan, GdsPlacementReport report)
    {
        if (plan.TopCellWaveguidePolygons.Count > 0)
        {
            report.Warnings.Add(
                $"Top-cell routing geometry ({plan.TopCellWaveguidePolygons.Count} waveguide " +
                "polygon(s)) was not imported: no group was created to hold the frozen paths.");
        }
        if (plan.TopCellResidualPolygons.Count > 0)
        {
            report.Warnings.Add(
                $"Top-cell background geometry ({plan.TopCellResidualPolygons.Count} polygon(s)) " +
                "was not imported: no group was created to hold it.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Margin (µm) between the imported design's outer edge and the auto-enlarged
    /// chip boundary, so routed connections have room around the outermost pins.
    /// </summary>
    public const double ChipFitMarginUm = 100.0;

    /// <summary>
    /// The uniform translation the whole import receives so it lands in free space:
    /// when the canvas already holds components, the import's bounding-box top-left
    /// moves to (existing maxX + <see cref="ExistingContentMarginUm"/>, existing minY)
    /// — right of the existing content, top-aligned with it. Every placement shifts
    /// by the same delta, so the import's internal relative geometry (abutment!)
    /// stays exact. On an empty canvas the raw GDS coordinates are kept, EXCEPT
    /// that a negative origin is shifted to (0, 0): the chip is anchored at the
    /// origin, and content at negative coordinates could never be routed or moved.
    /// </summary>
    private (double X, double Y) ComputeImportOriginOffset(GdsPlacementPlan plan)
    {
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

        var existing = BoundingBoxCalculator.Calculate(_canvas.Components);
        if (existing is null)
            return (Math.Max(0.0, -importMinX), Math.Max(0.0, -importMinY));

        return (existing.Value.MaxX + ExistingContentMarginUm - importMinX,
                existing.Value.MinY - importMinY);
    }

    /// <summary>
    /// Enlarges the chip playfield (and with it the A* routing grid) when the
    /// canvas content exceeds it after placement — imported designs are often
    /// bigger than the configured default chip. Capped at
    /// <see cref="CAP_Core.Grid.ChipSizeConfiguration.MaxDimensionMicrometers"/>;
    /// the applied size lands in the report so the dialog can sync the
    /// chip-size settings panel.
    /// </summary>
    private void EnsureDesignFitsChip(GdsPlacementReport report)
    {
        var content = BoundingBoxCalculator.Calculate(_canvas.Components);
        if (content is null)
            return;

        var neededWidth = content.Value.MaxX + ChipFitMarginUm;
        var neededHeight = content.Value.MaxY + ChipFitMarginUm;
        if (neededWidth <= _canvas.ChipMaxX && neededHeight <= _canvas.ChipMaxY)
            return;

        var maxUm = CAP_Core.Grid.ChipSizeConfiguration.MaxDimensionMicrometers;
        var newWidth = Math.Min(Math.Max(neededWidth, _canvas.ChipMaxX), maxUm);
        var newHeight = Math.Min(Math.Max(neededHeight, _canvas.ChipMaxY), maxUm);

        _canvas.ChipMinX = 0;
        _canvas.ChipMinY = 0;
        _canvas.ChipMaxX = newWidth;
        _canvas.ChipMaxY = newHeight;
        _canvas.InitializeAStarRouting(0, 0, newWidth, newHeight);

        report.ChipEnlargedToWidthUm = newWidth;
        report.ChipEnlargedToHeightUm = newHeight;
        report.Warnings.Add(string.Create(CultureInfo.InvariantCulture,
            $"Chip enlarged to {newWidth / 1000.0:0.##} × {newHeight / 1000.0:0.##} mm to fit the imported design."));
        if (neededWidth > maxUm || neededHeight > maxUm)
        {
            report.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                "The imported design exceeds the maximum chip size ({0:0.#} mm) — " +
                "content beyond the boundary cannot be routed or moved.",
                maxUm / 1000.0));
        }
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

using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using BendSegment = CAP_Core.Routing.BendSegment;
using PathSegment = CAP_Core.Routing.PathSegment;
using Shouldly;
using UnitTests.Export;
using UnitTests.Services.GdsImport;
using Xunit;
using AvaloniaFactAttribute = Avalonia.Headless.XUnit.AvaloniaFactAttribute;

namespace UnitTests.UI;

/// <summary>
/// Visual-verification render pipeline for the GDS round-trip (PR media in
/// <c>docs/pr-media/gds-import/</c>, following the <c>label-declutter</c>
/// pattern of step-ordered PNGs + manifest.json). Runs the loop of
/// <see cref="GdsHighestLevelRoundTripTests"/> — the real 7-component
/// mixed-PDK user design (<see cref="GdsUserDesignFixture"/>) exported with the
/// app's exporter (real nazca), read back with the app's own
/// <see cref="GdsReader"/>/<see cref="GdsCellFlattener"/>, and re-imported
/// through the button's service path — and renders four pixel-aligned panels:
/// <list type="number">
/// <item>the ORIGINAL design on the production canvas renderers,</item>
/// <item>the exported GDS as an independent ground truth (own reader,</item>
/// <item>the re-imported design (auto-connect OFF, honest v1: no connections),</item>
/// <item>the re-imported design (auto-connect ON: the 2 restored connections).</item>
/// </list>
/// <para>
/// Render path: the real <c>DesignCanvas</c> control is NOT headless-feasible
/// (it needs the full App DI stack — documented at
/// <see cref="CanvasLabelDeclutterSceneControl"/>), so panels 1/3/4 reuse that
/// established scene control, which composes the same production renderers
/// (<c>WaveguideConnectionRenderer</c> then <c>ComponentRenderer</c>, then the
/// deferred label flush) in the same order <c>DesignCanvas.Render</c> calls
/// them. Panel 2 is drawn by <see cref="GdsGroundTruthRenderer"/> straight into
/// a <see cref="RenderTargetBitmap"/>.
/// </para>
/// <para>
/// All four panels share one world window (union of the canvas content and the
/// flipped GDS bounding box, plus margin) and one scale; panels 3/4 use the
/// same window translated by the import's re-origin shift (anchored on the
/// mmi2x2_dp placement like the round-trip test), so every component sits at
/// the same pixel in all four images.
/// </para>
/// <para>
/// Environment guard: needs a Python with nazca (SiEPIC-upgraded for the
/// auto-connect pins). This is an <see cref="AvaloniaFactAttribute"/> test —
/// Xunit.SkippableFact's <c>Skip.If</c> cannot report a skip through
/// AvaloniaFact's own test-case discoverer, so a missing engine is an early
/// return here, matching the repo's other environment-gated screenshot tests
/// (e.g. <c>ShowcaseCanvasVsGdsScreenshotTests</c>).
/// </para>
/// </summary>
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class GdsRoundTripVisualVerificationTests : IDisposable
{
    private const string TopCellName = "ConnectAPIC_Design";

    /// <summary>Panel width in pixels; the height follows from the shared world window's aspect.</summary>
    private const int PanelWidthPx = 1400;

    /// <summary>Empty world-space margin (µm) around the fitted content on every side.</summary>
    private const double MarginUm = 25;

    /// <summary>Half-extent (px) of the square pixel regions probed by the fill assertions.</summary>
    private const int ProbeHalfExtentPx = 4;

    /// <summary>Half-extent (px) of the square pixel regions probed by the route assertions —
    /// wider, so a dashed blocked-fallback route's dash gaps never swallow a probe whole.</summary>
    private const int RouteProbeHalfExtentPx = 7;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-visual-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [AvaloniaFact]
    public async Task RenderGdsRoundTripPanels_GeneratesVerificationImages()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        if (python is null)
            return; // no nazca engine on this machine — see the class summary for why this is not Skip.If.

        // ── 1. Build + export the original design (same path as the round-trip test) ──
        // Task.Run: the fixture ends with RecalculateRoutesAsync().GetAwaiter().GetResult() —
        // a blocking wait that deadlocks on this test's Avalonia UI thread (the routing
        // continuation posts back to the captured UI synchronization context). Off the UI
        // thread the plain xUnit context lets the blocking wait complete.
        var original = await Task.Run(GdsUserDesignFixture.BuildUserDesignCanvas);
        var skippedConnections = new List<string>();
        var exportWarnings = new List<string>();
        var script = new SimpleNazcaExporter().Export(
            original, skippedConnections: skippedConnections, exportWarnings: exportWarnings);
        skippedConnections.ShouldBeEmpty("all 10 routes are real, exportable geometry");
        exportWarnings.ShouldBeEmpty();

        var exportDir = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "user_design.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}");
        bool siepicUpgraded = run.StdOut.Contains("SiEPIC cell(s) upgraded", StringComparison.Ordinal);

        // ── 2. Read the file back with OUR reader (the ground truth under test) ──
        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        var flattener = new GdsCellFlattener(library);
        var gdsWorldBox = FlipToWorld(flattener.GetBoundingBox(TopCellName));

        // ── 3. The shared world window: content union + margin, one scale for all panels ──
        var shared = Inflate(Union(CanvasContentBox(original), gdsWorldBox), MarginUm);
        int panelHeightPx = (int)Math.Ceiling(PanelWidthPx * shared.Height / shared.Width);
        double scale = PanelWidthPx / shared.Width;
        var panelSize = new PixelSize(PanelWidthPx, panelHeightPx);

        var outDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.png"))
            File.Delete(stale);

        // ── 4. Panel 01: the original canvas through the production renderers ──
        using var panel1 = CaptureCanvas(original, shared, panelSize);
        string path1 = Path.Combine(outDir, "01-original-canvas.png");
        ScreenshotArtifacts.SavePng(panel1, path1);

        // ── 5. Panel 02: the exported GDS, own reader, same window ──
        using var panel2 = GdsGroundTruthRenderer.RenderTopCell(library, flattener, TopCellName, shared, panelSize);
        string path2 = Path.Combine(outDir, "02-exported-gds-groundtruth.png");
        ScreenshotArtifacts.SavePng(panel2, path2);

        // ── 6. Analyze + explode-import through the button's service path ──
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { TopCellName });
        var sink = new GdsUserDesignFixture.LibrarySink(Path.Combine(_root, "prefs.json"));
        var service = new GdsImportService(
            GdsUserDesignFixture.CreateStore(_root, "pdks"), () => sink.Templates.ToList(), sink.Register);
        var dialogOptions = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10), (501, 1)] },
        };
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], dialogOptions, null);
        outcome.Instances.Count.ShouldBe(7);
        var plan = GdsPlacementPlan.FromOutcome(outcome);

        // ── 7. Place twice on fresh canvases: auto-connect OFF vs ON (radius 200 µm default) ──
        var canvasOff = new DesignCanvasViewModel();
        canvasOff.InitializeAStarRouting(150, -700, 950, -250);
        var reportOff = await new GdsPlacementExecutor(canvasOff, null, () => sink.Templates.ToList())
            .ExecuteAsync(plan, autoConnectFreePins: false);

        var canvasOn = new DesignCanvasViewModel();
        canvasOn.InitializeAStarRouting(150, -700, 950, -250);
        var reportOn = await new GdsPlacementExecutor(canvasOn, null, () => sink.Templates.ToList())
            .ExecuteAsync(plan, autoConnectFreePins: true);

        // ── 8. Panels 03/04 in the same window, translated by the re-origin shift ──
        var (shiftX, shiftY) = ComputeReoriginShift(original, canvasOff);
        var importedWorld = new Rect(shared.X - shiftX, shared.Y - shiftY, shared.Width, shared.Height);

        using var panel3 = CaptureCanvas(canvasOff, importedWorld, panelSize);
        string path3 = Path.Combine(outDir, "03-reimported-canvas.png");
        ScreenshotArtifacts.SavePng(panel3, path3);

        using var panel4 = CaptureCanvas(canvasOn, importedWorld, panelSize);
        string path4 = Path.Combine(outDir, "04-reimported-autoconnect.png");
        ScreenshotArtifacts.SavePng(panel4, path4);

        WriteManifest(outDir);

        // ── 9. Sanity + per-panel pixel assertions ──
        reportOff.PlacedCount.ShouldBe(7);
        reportOff.GroupCreated.ShouldBeTrue();
        reportOff.ConnectedCount.ShouldBe(0, "no abutment connections exist in this spaced design");
        reportOff.AutoConnectedCount.ShouldBe(0);
        reportOn.PlacedCount.ShouldBe(7);
        reportOn.AutoConnectedCount.ShouldBe(siepicUpgraded ? 2 : 0);

        var probes = new Probes(original, canvasOff, canvasOn, shared, importedWorld, scale);
        AssertPanel01(panel1, path1, panelSize, probes);
        AssertPanel02(panel2, path2, panelSize, probes);
        AssertPanel03(panel3, path3, panelSize, probes);
        AssertPanel04(panel4, path4, panelSize, probes, reportOn, siepicUpgraded,
            CountExternalPinPixels(panel3, panelSize));
    }

    // ── World-window helpers ─────────────────────────────────────────────────

    /// <summary>Union of the canvas' component footprints and connection endpoints (Y-down µm).</summary>
    private static Rect CanvasContentBox(DesignCanvasViewModel canvas)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var component in canvas.Components)
        {
            Include(component.X, component.Y);
            Include(component.X + component.Width, component.Y + component.Height);
        }
        foreach (var connection in canvas.Connections)
        {
            Include(connection.StartX, connection.StartY);
            Include(connection.EndX, connection.EndY);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Flips a Y-up GDS bounding box into the canvas' Y-down world frame (worldY = −gdsY).</summary>
    private static Rect FlipToWorld(GdsBoundingBox box) =>
        new(box.MinX, -box.MaxY, box.Width, box.Height);

    private static Rect Union(Rect a, Rect b) => a.Union(b);

    private static Rect Inflate(Rect rect, double margin) =>
        new(rect.X - margin, rect.Y - margin, rect.Width + 2 * margin, rect.Height + 2 * margin);

    /// <summary>
    /// The import re-origins at the layout's top-left; this returns the uniform
    /// shift between the original and the placed design, anchored on the exact
    /// mmi2x2_dp placement (the same anchor
    /// <see cref="GdsHighestLevelRoundTripTests"/> uses; pairing is by component
    /// class + position rank).
    /// </summary>
    private static (double Dx, double Dy) ComputeReoriginShift(
        DesignCanvasViewModel original, DesignCanvasViewModel imported)
    {
        var originalMmi = RankByPosition(original.Components.Select(vm => vm.Component)
            .Where(c => c.HumanReadableName == "2x2 MMI Coupler"))[1];
        originalMmi.PhysicalY.ShouldBeLessThan(-500, "the rank-1 MMI is the northern one (mmi1)");

        var group = imported.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        var placedMmi = RankByPosition(group.GetAllComponentsRecursive()
            .Where(c => CellKeyOf(c) == "mmi2x2_dp"))[1];
        return (originalMmi.PhysicalX - placedMmi.PhysicalX, originalMmi.PhysicalY - placedMmi.PhysicalY);
    }

    /// <summary>Orders components by placement X then Y — the round-trip test's rank pairing.</summary>
    private static List<Component> RankByPosition(IEnumerable<Component> components) =>
        components.OrderBy(c => c.PhysicalX).ThenBy(c => c.PhysicalY).ToList();

    /// <summary>Strips the fabricated "nazca_" prefix of imported raw-code drafts (see the round-trip test).</summary>
    private static string CellKeyOf(Component component)
    {
        var name = component.HumanReadableName ?? string.Empty;
        return name.StartsWith("nazca_", StringComparison.Ordinal) ? name["nazca_".Length..] : name;
    }

    // ── Probes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// World-space sample points for the pixel assertions, in each panel's own
    /// frame: the mmi1 footprint centre (filled in every panel), per-connection
    /// probe points sampled ON the routed segments of the original design
    /// (panel 1 renders them; panel 2 must contain the same centerlines as
    /// flattened waveguide polygons), and the frozen group-internal path probe
    /// points of the auto-connect canvas (panel 4 — empty when the scenario
    /// restores nothing). Probe points lie on the segment geometry itself
    /// (straight-segment quarter points, arc points from each bend's own
    /// centre/radius/sweep) rather than at pin midpoints, because the routes
    /// bend — the halfring↔adiabatic span is even routed as a full 360° circle
    /// in this layout (the bend math degenerates when the 10.6 µm pin span is
    /// shorter than the bend diameter — visible as the ring in panels 1/2/4).
    /// Pin endpoints are never probed: pin glyphs paint over them.
    /// </summary>
    private sealed class Probes
    {
        internal Probes(
            DesignCanvasViewModel original, DesignCanvasViewModel importedOff, DesignCanvasViewModel importedOn,
            Rect shared, Rect importedWorld, double scale)
        {
            Shared = shared;
            ImportedWorld = importedWorld;
            Scale = scale;

            var originals = original.Components.Select(vm => vm.Component).ToList();
            var originalMmi1 = RankByPosition(originals.Where(c => c.HumanReadableName == "2x2 MMI Coupler"))[1];
            MmiCenter = new Point(
                originalMmi1.PhysicalX + originalMmi1.WidthMicrometers / 2,
                originalMmi1.PhysicalY + originalMmi1.HeightMicrometers / 2);

            OriginalRouteProbes = original.Connections
                .Select(vm => RouteProbePoints(vm.Connection))
                .ToList();
            OriginalRouteProbes.Count.ShouldBe(10, "his ten waveguide connections");

            var originalCrossings = RankByPosition(originals.Where(c => c.HumanReadableName == "Crossing 4-Port"));
            var crossingConnection = FindConnection(original,
                originalCrossings[1], "port 1", originalCrossings[0], "port 2");
            CrossingRouteProbes = RouteProbePoints(crossingConnection);
            CrossingConnectionIndex = original.Connections
                .Select((vm, index) => (vm, index))
                .Single(t => ReferenceEquals(t.vm.Connection, crossingConnection)).index;
            RingRouteProbes = RouteProbePoints(FindConnection(original,
                originals.Single(c => c.HumanReadableName == "DC Halfring-Straight"), "port 3",
                originals.Single(c => c.HumanReadableName == "Adiabatic Coupler TE 1550"), "port 2"));

            var groupOff = importedOff.Components.Single().Component.ShouldBeOfType<ComponentGroup>();
            var placedMmi1 = RankByPosition(groupOff.GetAllComponentsRecursive()
                .Where(c => CellKeyOf(c) == "mmi2x2_dp"))[1];
            PlacedMmiCenter = new Point(
                placedMmi1.PhysicalX + placedMmi1.WidthMicrometers / 2,
                placedMmi1.PhysicalY + placedMmi1.HeightMicrometers / 2);

            var groupOn = importedOn.Components.Single().Component.ShouldBeOfType<ComponentGroup>();
            FrozenRouteProbes = groupOn.InternalPaths
                .Select(path => RouteProbePoints(path.Path.Segments))
                .ToList();
        }

        internal Rect Shared { get; }
        internal Rect ImportedWorld { get; }
        internal double Scale { get; }

        /// <summary>mmi1 centre in the original/GDS frame (panels 1/2).</summary>
        internal Point MmiCenter { get; }

        /// <summary>Placed mmi1 centre in the re-origined frame (panels 3/4).</summary>
        internal Point PlacedMmiCenter { get; }

        /// <summary>Probe points on every original connection's routed path (panel-1 visibility check).</summary>
        internal IReadOnlyList<IReadOnlyList<Point>> OriginalRouteProbes { get; }

        /// <summary>Probe points on the crossing↔crossing route (panels 1/2).</summary>
        internal IReadOnlyList<Point> CrossingRouteProbes { get; }

        /// <summary>
        /// Index (in canvas connection order) of the crossing↔crossing connection —
        /// the 12.8 µm straight span whose route is painted over by the two
        /// crossings' pin glyph clouds on the canvas (pins draw above
        /// connections), making it legitimately invisible in panel 1.
        /// </summary>
        internal int CrossingConnectionIndex { get; }

        /// <summary>Probe points on the halfring↔adiabatic route — the full-circle bend (panel 2).</summary>
        internal IReadOnlyList<Point> RingRouteProbes { get; }

        /// <summary>Probe points per restored (frozen group-internal) connection of the auto-connect canvas (panels 3/4).</summary>
        internal IReadOnlyList<IReadOnlyList<Point>> FrozenRouteProbes { get; }

        internal PixelRect AroundMmiCenter() => Around(MmiCenter, Shared, ProbeHalfExtentPx);

        internal PixelRect AroundPlacedMmiCenter() => Around(PlacedMmiCenter, ImportedWorld, ProbeHalfExtentPx);

        /// <summary>Route probe region: wider than <see cref="ProbeHalfExtentPx"/> so a dashed route's
        /// 5-on/3-off dash pattern (world units) can never fall entirely into an off-gap.</summary>
        internal PixelRect AroundSharedRoute(Point worldPoint) => Around(worldPoint, Shared, RouteProbeHalfExtentPx);

        /// <summary>See <see cref="AroundSharedRoute"/>.</summary>
        internal PixelRect AroundImportedRoute(Point worldPoint) => Around(worldPoint, ImportedWorld, RouteProbeHalfExtentPx);

        private PixelRect Around(Point worldPoint, Rect world, int halfExtentPx)
        {
            int cx = (int)Math.Round((worldPoint.X - world.X) * Scale);
            int cy = (int)Math.Round((worldPoint.Y - world.Y) * Scale);
            return new PixelRect(
                cx - halfExtentPx, cy - halfExtentPx,
                2 * halfExtentPx + 1, 2 * halfExtentPx + 1);
        }
    }

    /// <summary>
    /// Sample points guaranteed to lie on a connection's rendered route:
    /// quarter/mid/three-quarter points of straight segments and arc points at
    /// the same fractions of each bend's sweep (computed from the bend's own
    /// centre/radius — the exact convention <c>WaveguideConnectionRenderer</c>'s
    /// arc polyline uses), PLUS quarter points of the straight pin-to-pin chord,
    /// which is what the renderer draws instead when the path is flagged stale
    /// (or when there are no segments at all). Pin endpoints are skipped — pin
    /// glyphs paint over them.
    /// </summary>
    private static IReadOnlyList<Point> RouteProbePoints(WaveguideConnection connection)
    {
        var (sx, sy) = connection.StartPin.GetAbsolutePosition();
        var (ex, ey) = connection.EndPin.GetAbsolutePosition();
        var points = new List<Point>
        {
            Lerp((sx, sy), (ex, ey), 0.25),
            Lerp((sx, sy), (ex, ey), 0.5),
            Lerp((sx, sy), (ex, ey), 0.75),
        };
        points.AddRange(RouteProbePoints(connection.GetPathSegments()));
        return points;
    }

    /// <summary>Segment-geometry overload of <see cref="RouteProbePoints(WaveguideConnection)"/>.</summary>
    private static IReadOnlyList<Point> RouteProbePoints(IReadOnlyList<PathSegment> segments)
    {
        var points = new List<Point>();
        foreach (var segment in segments)
        {
            if (segment is BendSegment bend)
            {
                points.Add(OnArc(bend, 0.25));
                points.Add(OnArc(bend, 0.5));
                points.Add(OnArc(bend, 0.75));
            }
            else
            {
                points.Add(Lerp(segment.StartPoint, segment.EndPoint, 0.25));
                points.Add(Lerp(segment.StartPoint, segment.EndPoint, 0.5));
                points.Add(Lerp(segment.StartPoint, segment.EndPoint, 0.75));
            }
        }
        return points;
    }

    /// <summary>A point at <paramref name="fraction"/> of a bend's sweep, on the arc.</summary>
    private static Point OnArc(BendSegment bend, double fraction)
    {
        double angle = (bend.StartAngleDegrees + bend.SweepAngleDegrees * fraction) * Math.PI / 180.0;
        double sign = Math.Sign(bend.SweepAngleDegrees);
        return new Point(
            bend.Center.X + bend.RadiusMicrometers * Math.Cos(angle - Math.PI / 2 * sign),
            bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angle - Math.PI / 2 * sign));
    }

    private static Point Lerp((double X, double Y) a, (double X, double Y) b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>Finds the connection between two named pins of two components (either direction).</summary>
    private static WaveguideConnection FindConnection(
        DesignCanvasViewModel canvas, Component a, string pinA, Component b, string pinB)
    {
        var pa = Pin(a, pinA);
        var pb = Pin(b, pinB);
        return canvas.Connections
            .Select(vm => vm.Connection)
            .Single(c => (ReferenceEquals(c.StartPin, pa) && ReferenceEquals(c.EndPin, pb)) ||
                         (ReferenceEquals(c.StartPin, pb) && ReferenceEquals(c.EndPin, pa)));
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    // ── Per-panel pixel assertions ───────────────────────────────────────────

    /// <summary>
    /// Panel 01: component bodies at the user's coordinates, every one of his 10
    /// connections visible (healthy routes in orange, the blocked-fallback
    /// detours of this tightly-overlapping layout as red dashes), name labels.
    /// </summary>
    private static void AssertPanel01(WriteableBitmap panel, string path, PixelSize size, Probes probes)
    {
        AssertFileIsRealPng(path);
        CountDistinctSampledColors(panel).ShouldBeGreaterThan(8,
            "bodies + borders + pins + labels + routes on the dark background");

        CountWhere(panel, probes.AroundMmiCenter(), IsComponentBody)
            .ShouldBeGreaterThan(40, "the mmi1 footprint is the plain component body fill (40,50,70)");

        // The crossing↔crossing span (12.8 µm straight) is skipped: its route is
        // genuinely painted over by the two crossings' pin glyph clouds at this
        // scale — it exists, the pin glyphs just draw above it. Its presence in
        // the exported GDS is verified by the panel-02 waveguide probe instead.
        var invisibleRoutes = Enumerable.Range(0, probes.OriginalRouteProbes.Count)
            .Where(i => i != probes.CrossingConnectionIndex)
            .Where(i => !probes.OriginalRouteProbes[i]
                .Any(p => CountWhere(panel, probes.AroundSharedRoute(p), IsRoutePixel) > 0))
            .Select(i => $"#{i} (first probes: {string.Join("; ", probes.OriginalRouteProbes[i].Take(3))})")
            .ToList();
        invisibleRoutes.ShouldBeEmpty(
            "every connection except the pin-glyph-covered crossing span renders a visible route "
            + "(orange or red-dashed); misses: " + string.Join(" | ", invisibleRoutes));
        CountWhere(panel, Full(size), IsCanvasOrange)
            .ShouldBeGreaterThan(500, "the healthy A*-routed connections are drawn in orange");
        CountWhere(panel, Full(size), IsBlockedRed)
            .ShouldBeGreaterThan(300, "the blocked-fallback detours render as red dashes — " +
                "several of his connections cannot route cleanly through the overlapping app-template footprints");

        CountWhere(panel, Full(size), IsWhiteishText)
            .ShouldBeGreaterThan(100, "component name labels are rendered");
        AssertEmptyCorner(panel, size);
    }

    /// <summary>Panel 02: the ground-truth GDS — device fills, bronze waveguides (incl. the full-circle ring), empty background.</summary>
    private static void AssertPanel02(RenderTargetBitmap panel, string path, PixelSize size, Probes probes)
    {
        AssertFileIsRealPng(path);

        CountWhere(panel, probes.AroundMmiCenter(), IsDeviceFill)
            .ShouldBeGreaterThan(30, "the mmi1 device body covers its footprint centre");
        probes.CrossingRouteProbes.Any(p => CountWhere(panel, probes.AroundSharedRoute(p), IsBronzeWaveguide) > 0)
            .ShouldBeTrue("the crossing↔crossing waveguide is flattened top-cell geometry in the file");
        probes.RingRouteProbes.Any(p => CountWhere(panel, probes.AroundSharedRoute(p), IsBronzeWaveguide) > 0)
            .ShouldBeTrue("the halfring↔adiabatic route — the full-circle bend — is in the file");
        CountWhere(panel, Full(size), IsBronzeWaveguide)
            .ShouldBeGreaterThan(300, "the ten routed waveguides are flattened top-cell polygons");
        CountWhere(panel, Full(size), IsDeviceFill)
            .ShouldBeGreaterThan(2000, "the five device cells contribute real foundry geometry");
        AssertEmptyCorner(panel, size);
    }

    /// <summary>Panel 03: the re-imported group — GDS outline fills, free green pins, NO connections.</summary>
    private static void AssertPanel03(WriteableBitmap panel, string path, PixelSize size, Probes probes)
    {
        AssertFileIsRealPng(path);

        CountWhere(panel, probes.AroundPlacedMmiCenter(), IsOutlineFill)
            .ShouldBeGreaterThan(30, "the placed mmi1 renders its imported GDS outline fill");
        CountExternalPinPixels(panel, size)
            .ShouldBeGreaterThan(30, "all 28 pins are free — every unoccupied group pin renders green");
        CountWhere(panel, Full(size), IsAnyRoutePixel)
            .ShouldBe(0, "auto-connect OFF: the honest v1 state has no connections at all — " +
                "the same spots where panel 04 shows the restored routes must be empty here");
        foreach (var points in probes.FrozenRouteProbes)
            points.Any(p => CountWhere(panel, probes.AroundImportedRoute(p), IsAnyRoutePixel) > 0)
                .ShouldBeFalse("no route at the spots where auto-connect would later restore them");
        AssertEmptyCorner(panel, size);
    }

    /// <summary>
    /// Panel 04: auto-connect ON — exactly the 2 restored connections appear as
    /// orange group-internal paths (one of them re-routed as the same full
    /// circle the original design carries), and their 4 pins leave the
    /// free-pin set.
    /// </summary>
    private static void AssertPanel04(
        WriteableBitmap panel, string path, PixelSize size, Probes probes,
        GdsPlacementReport reportOn, bool siepicUpgraded, int panel3PinPixels)
    {
        AssertFileIsRealPng(path);

        if (!siepicUpgraded)
        {
            // Bare-nazca environment: the ambiguity guard refuses every twin-poisoned
            // pair — the panel must honestly show zero restored connections.
            probes.FrozenRouteProbes.ShouldBeEmpty();
            CountWhere(panel, Full(size), IsAnyRoutePixel).ShouldBe(0);
            return;
        }

        reportOn.AutoConnectedCount.ShouldBe(2);
        probes.FrozenRouteProbes.Count.ShouldBe(2, "the two restored connections are frozen into the group");
        foreach (var points in probes.FrozenRouteProbes)
            points.Any(p => CountWhere(panel, probes.AroundImportedRoute(p), IsFrozenOrange) > 0)
                .ShouldBeTrue("each restored connection renders as an orange frozen path " +
                    "(crossing↔crossing 12.8 µm, halfring↔adiabatic 10.6 µm)");
        CountWhere(panel, Full(size), IsFrozenOrange)
            .ShouldBeGreaterThan(50, "the two restored spans are drawn as frozen group-internal paths");
        CountExternalPinPixels(panel, size)
            .ShouldBeLessThan(panel3PinPixels, "the 2 restored connections occupy 4 of the 28 free pins");
        AssertEmptyCorner(panel, size);
    }

    /// <summary>Count of light-green unoccupied-group-pin pixels in the whole panel.</summary>
    private static int CountExternalPinPixels(WriteableBitmap panel, PixelSize size) =>
        CountWhere(panel, Full(size), IsExternalPinGreen);

    /// <summary>The bottom-left corner lies inside the margin in every panel: pure background.</summary>
    private static void AssertEmptyCorner(WriteableBitmap panel, PixelSize size) =>
        CountWhere(panel, EmptyCorner(size), IsBackground)
            .ShouldBeGreaterThan(90, "the margin corner stays empty background");

    private static void AssertEmptyCorner(RenderTargetBitmap panel, PixelSize size) =>
        CountWhere(panel, EmptyCorner(size), IsBackground)
            .ShouldBeGreaterThan(90, "the margin corner stays empty background");

    private static PixelRect EmptyCorner(PixelSize size) => new(8, size.Height - 18, 10, 10);

    private static PixelRect Full(PixelSize size) => new(0, 0, size.Width, size.Height);

    private static void AssertFileIsRealPng(string path)
    {
        File.Exists(path).ShouldBeTrue($"{path} was written");
        new FileInfo(path).Length.ShouldBeGreaterThan(20_000, $"{path} is a real render, not a stub");
    }

    // ── Pixel predicates (BGRA bytes; background is #1E1E1E = (30,30,30)) ────

    /// <summary>Plain component body fill (40,50,70) of non-outlined canvas components.</summary>
    private static bool IsComponentBody(byte r, byte g, byte b) =>
        Math.Abs(r - 40) <= 8 && Math.Abs(g - 50) <= 8 && Math.Abs(b - 70) <= 10;

    /// <summary>
    /// Core pixels of the canvas waveguide pen (#FFA500 = (255,165,0)). Kept
    /// tight on purpose: subpixel-anti-aliased text fringes produce orange-ish
    /// blends like (210,153,45) and (225,172,49) that a looser predicate would
    /// mistake for routes.
    /// </summary>
    private static bool IsCanvasOrange(byte r, byte g, byte b) =>
        r >= 245 && g >= 130 && g <= 190 && b <= 60;

    /// <summary>
    /// Core pixels of the frozen group-internal path pen (ARGB 200,255,140,0
    /// over the dark background ≈ (206,116,7)). The blue ceiling is the
    /// discriminator against text fringes ((195,120,34) and friends all carry
    /// b ≥ 20); the frozen stroke is wide enough to always leave full-coverage
    /// core pixels.
    /// </summary>
    private static bool IsFrozenOrange(byte r, byte g, byte b) =>
        r >= 195 && r <= 220 && g >= 105 && g <= 130 && b <= 15;

    /// <summary>Blocked-fallback route pixels (red pen #FF0000, dashed).</summary>
    private static bool IsBlockedRed(byte r, byte g, byte b) =>
        r >= 200 && g <= 70 && b <= 70;

    /// <summary>Any top-level canvas route pixel, healthy or blocked (panel-1 probes).</summary>
    private static bool IsRoutePixel(byte r, byte g, byte b) =>
        IsCanvasOrange(r, g, b) || IsBlockedRed(r, g, b);

    /// <summary>Any route pixel including frozen group-internal ones (panel-3 absence check).</summary>
    private static bool IsAnyRoutePixel(byte r, byte g, byte b) =>
        IsRoutePixel(r, g, b) || IsFrozenOrange(r, g, b);

    /// <summary>White-ish text pixels (name labels, pin labels).</summary>
    private static bool IsWhiteishText(byte r, byte g, byte b) => r >= 200 && g >= 200 && b >= 200;

    /// <summary>
    /// Device-fill pixels of the ground-truth pane: steel blue (94,123,166) or
    /// plum (107,94,122) — both blue-dominant, unlike bronze (red-dominant) or
    /// the neutral background.
    /// </summary>
    private static bool IsDeviceFill(byte r, byte g, byte b) => b >= r + 10;

    /// <summary>
    /// Muted-bronze waveguide pixels (fill 176,120,64) of the ground-truth pane,
    /// tolerant of the anti-aliased blend the sub-pixel-wide waveguide polygons
    /// produce against the dark background (still red-dominant, unlike the
    /// blue-dominant device fills).
    /// </summary>
    private static bool IsBronzeWaveguide(byte r, byte g, byte b) =>
        r >= 100 && r >= b + 30 && g >= b + 10 && g <= 170 && b <= 130;

    /// <summary>
    /// GDS outline fill (ARGB 46,100,160,220) over the dark background blends to
    /// ≈(43,53,64); overlapping outline polygons push it toward ≈(53,73,92).
    /// Blue-dominant, brighter than the background, distinct from the plain
    /// body fill (which panel 3/4 never draws — every placed child is outlined).
    /// </summary>
    private static bool IsOutlineFill(byte r, byte g, byte b) =>
        b >= r + 15 && g >= r && b >= 55 && b <= 170;

    /// <summary>Light-green unoccupied group pin fill (144,238,144).</summary>
    private static bool IsExternalPinGreen(byte r, byte g, byte b) =>
        g >= 190 && r >= 100 && r <= 190 && b >= 100 && b <= 190;

    /// <summary>The shared dark canvas background.</summary>
    private static bool IsBackground(byte r, byte g, byte b) => r <= 40 && g <= 40 && b <= 40;

    // ── Pixel plumbing ───────────────────────────────────────────────────────

    /// <summary>
    /// Counts matching pixels of a <see cref="WriteableBitmap"/> region. The
    /// channel order follows the bitmap's own <see cref="PixelFormat"/> — a
    /// captured headless frame is not guaranteed to be BGRA (e.g. it is
    /// Rgba8888 here), and reading it as BGRA would swap red and blue.
    /// </summary>
    private static int CountWhere(WriteableBitmap bitmap, PixelRect region, Func<byte, byte, byte, bool> matches)
    {
        region = region.Intersect(Full(bitmap.PixelSize));
        if (region.Width <= 0 || region.Height <= 0)
            return 0;

        int count = 0;
        using var fb = bitmap.Lock();
        bool redFirst = fb.Format == PixelFormat.Rgba8888;
        (redFirst || fb.Format == PixelFormat.Bgra8888).ShouldBeTrue(
            $"unexpected WriteableBitmap pixel format '{fb.Format}' — channel order unknown");
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = fb.Address + y * fb.RowBytes;
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                byte first = Marshal.ReadByte(row, x * 4);
                byte green = Marshal.ReadByte(row, x * 4 + 1);
                byte third = Marshal.ReadByte(row, x * 4 + 2);
                byte red = redFirst ? first : third;
                byte blue = redFirst ? third : first;
                if (matches(red, green, blue))
                    count++;
            }
        }
        return count;
    }

    /// <summary>Counts matching pixels of a <see cref="RenderTargetBitmap"/> region (BGRA layout).</summary>
    private static int CountWhere(RenderTargetBitmap bitmap, PixelRect region, Func<byte, byte, byte, bool> matches)
    {
        region = region.Intersect(Full(bitmap.PixelSize));
        if (region.Width <= 0 || region.Height <= 0)
            return 0;

        int bufferSize = region.Width * region.Height * 4;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(region, buffer, bufferSize, region.Width * 4);
            int count = 0;
            for (int i = 0; i < region.Width * region.Height; i++)
            {
                byte blue = Marshal.ReadByte(buffer, i * 4);
                byte green = Marshal.ReadByte(buffer, i * 4 + 1);
                byte red = Marshal.ReadByte(buffer, i * 4 + 2);
                if (matches(red, green, blue))
                    count++;
            }
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Distinct-color census over a coarse sample grid (near-blank render tripwire).</summary>
    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        int stepX = Math.Max(1, fb.Size.Width / 64);
        int stepY = Math.Max(1, fb.Size.Height / 64);
        var colors = new HashSet<int>();
        for (int y = 0; y < fb.Size.Height; y += stepY)
        {
            var row = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < fb.Size.Width; x += stepX)
                colors.Add(Marshal.ReadInt32(row, x * 4));
        }
        return colors.Count;
    }

    // ── Capture + output ─────────────────────────────────────────────────────

    /// <summary>
    /// Renders <paramref name="canvas"/> through the production renderer
    /// composition (<see cref="CanvasLabelDeclutterSceneControl"/>) inside a
    /// headless window and captures the frame — the walkthrough tests' capture
    /// path (<see cref="CanvasLabelDeclutterWalkthroughTests"/>).
    /// </summary>
    private static WriteableBitmap CaptureCanvas(DesignCanvasViewModel canvas, Rect world, PixelSize panelSize)
    {
        var scene = new CanvasLabelDeclutterSceneControl(canvas, new CanvasInteractionState(), world)
        {
            Width = panelSize.Width,
            Height = panelSize.Height,
        };
        var window = new Window
        {
            Width = scene.Width,
            Height = scene.Height,
            Content = scene,
            Background = Brushes.Black,
        };
        window.Show();
        try
        {
            WriteableBitmap? bitmap = null;
            for (var attempt = 0; attempt < 3 && bitmap == null; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                bitmap = window.CaptureRenderedFrame();
            }
            bitmap.ShouldNotBeNull("CaptureRenderedFrame stayed null after 3 attempts");
            return bitmap;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Writes the manifest next to the panels (same {file, caption} format as label-declutter).</summary>
    private static void WriteManifest(string outDir)
    {
        var manifest = new List<object>
        {
            new
            {
                file = "01-original-canvas.png",
                caption = "The ORIGINAL user design rendered by the production canvas renderers "
                    + "(WaveguideConnectionRenderer + ComponentRenderer — the composition DesignCanvas.Render "
                    + "uses): 7 components from two PDKs (2× demofab 2x2 MMI; SiEPIC adiabatic coupler, "
                    + "broadband DC, 2× crossing 4-port, DC halfring) at the user's exact coordinates, joined "
                    + "by his 10 A*-routed waveguide connections. Dark #1E1E1E background, Y-down µm world. "
                    + "Look at: healthy routes in orange; several connections drawn as RED DASHED blocked-"
                    + "fallback detours (the overlapping app-template footprints leave no clean path — they "
                    + "still export as real geometry); the halfring↔adiabatic connection routed as a FULL "
                    + "CIRCLE (left, below the halfring pins — the bend math degenerates when the 10.6 µm pin "
                    + "span is shorter than the bend diameter); the 12.8 µm crossing↔crossing span is hidden "
                    + "under the two crossings' pin glyph clouds (pins draw above connections). The two MMI "
                    + "bodies are large app-template footprints that overlap the smaller SiEPIC components.",
            },
            new
            {
                file = "02-exported-gds-groundtruth.png",
                caption = "Independent ground truth of what actually landed in the exported GDS: the file read "
                    + "back with Lunima's OWN GdsReader + GdsCellFlattener (not gdstk/klayout) and flattened. "
                    + "Bronze = the 10 routed waveguides (nazca flattens them into the top cell — including the "
                    + "full-circle ring left of centre and the 12.8 µm crossing waveguide that panel 01 hides "
                    + "under pin glyphs), steel blue = device bodies on the waveguide layer (1,0), plum = "
                    + "other foundry layers (the two big demofab MMI bodies), gray dots = pin-label anchors "
                    + "(1,10 / 501,1). Same world window and scale as 01 — every device and every waveguide "
                    + "must sit at the same pixels as in the canvas render.",
            },
            new
            {
                file = "03-reimported-canvas.png",
                caption = "The re-imported design (GdsImportService + GdsPlacementExecutor, auto-connect OFF): "
                    + "the 'ConnectAPIC_Design' group (cyan selection border — the group lands selected, as "
                    + "after a real import) wraps the 7 placed instances, each drawn from its imported GDS "
                    + "outline polygons (blue fill, incl. the demofab 'mmi2x2_dp' name stubs etched into the "
                    + "MMI cells) with its name; light-green dots = the 28 free group pins. NO connections — "
                    + "the routed waveguides became flattened top-cell geometry, invisible to the abutment "
                    + "matcher (honest v1). Same window/scale as 01 (uniform re-origin shift removed): every "
                    + "component must align with the original render within ~1 µm. Child name labels overlap "
                    + "where instances cluster — group children get no label decluttering.",
            },
            new
            {
                file = "04-reimported-autoconnect.png",
                caption = "Same import with auto-connect ON (radius 200 µm): exactly 2 of the 10 logical "
                    + "connections are restored (orange group-internal paths) — the short opposing-pin spans "
                    + "crossing↔crossing (12.8 µm, the small orange line between the two central crossings) "
                    + "and halfring↔adiabatic (10.6 µm, re-routed by the router as the same FULL CIRCLE the "
                    + "original design carries — compare 01/02); their 4 pins leave the free-pin set (fewer "
                    + "green dots than 03). Two more candidate pairs were vetoed as genuinely ambiguous rather "
                    + "than miswired; the remaining 8 connections stay unrestored in v1.",
            },
        };
        ScreenshotArtifacts.WriteText(
            Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Repo-root <c>docs/pr-media/gds-import</c> (walks up from the test output for the .sln).</summary>
    private static string ResolveOutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "docs", "pr-media", "gds-import");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "docs", "pr-media", "gds-import");
    }
}

using System.Globalization;

namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Turns a parsed GDS library into a pure-data circuit description
/// (<see cref="GdsCircuitImport"/>): component drafts for unknown cells, placed
/// instances, and connections reconstructed from routing structure and pin
/// positions. No canvas, <c>Component</c> or UI objects are created — the
/// service layer consumes the result.
///
/// Two modes (see <see cref="GdsHierarchyImportOptions.Mode"/>):
/// <list type="bullet">
/// <item><b>ExplodeHierarchy</b>: the top cell's direct children become
/// instances. Cells resolved by
/// <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/> reference the
/// existing PDK component; unknown cells become drafts whose outlines/pins
/// absorb their whole subtree (one level of components, matching
/// <see cref="GdsCellFlattener.GetInstanceTree"/>). Two cell kinds never become
/// drafts or instances — zero-geometry cells (empty flattened bbox) and our own
/// export artifacts (<see cref="GdsExportArtifactExpander.ArtifactCellNames"/>,
/// matched directly or through a pass-through wrapper); each skipped
/// cell produces ONE info note instead of a per-instance failure cascade.
/// The one exception: an export artifact that holds real cell references (the
/// mixed-backend nazca partial is NOT flattened — its device cells are SREFs
/// carrying port labels) is recursed into ONE level, so its children join the
/// placement set as if they were top-level instances
/// (<see cref="GdsExportArtifactExpander.TryExpandArtifactInstances"/>).</item>
/// <item><b>BlackBox</b>: the whole top cell becomes a single draft whose pins
/// are the port labels of the ENTIRE flattened hierarchy (nested subcell labels
/// promoted with their instance context, see
/// <see cref="GdsHierarchyImportSession.BuildBlackBoxDraft"/>).</item>
/// </list>
///
/// An explode draft's pins are the cell's OWN port labels (nested labels belong
/// to absorbed sub-cells) plus the edge heuristic over its fully flattened
/// geometry. The circuit's external ports, by contrast, are the top cell's own
/// port LABELS only (gdsfactory convention) — unlabeled geometry ends at the
/// layout boundary stay internal.
/// </summary>
public static class GdsHierarchyImporter
{
    /// <summary>
    /// Placeholder token inside <see cref="GdsCellDraft.RawCode"/> for the .gds
    /// file name. The service layer replaces it with the absolute path of the
    /// source .gds after copying it next to the user-PDK JSON (absolute because
    /// the raw-code executor runs the snippet from a temp file with an unrelated
    /// working directory, so a bare relative name would never resolve).
    /// </summary>
    public const string GdsFileNameToken = "{GdsFileName}";

    /// <summary>
    /// Imports <paramref name="topCellName"/> from <paramref name="library"/>.
    /// The work is CPU-bound and synchronous; the async signature matches the
    /// other import entry points and honors cancellation between stages.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The library contains no cells, the top cell is not defined, or the
    /// hierarchy is broken (undefined reference / cycle, from the flattener).
    /// </exception>
    public static Task<GdsCircuitImport> ImportAsync(
        GdsLibrary library,
        string topCellName,
        GdsHierarchyImportOptions? options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(topCellName);
        options ??= new GdsHierarchyImportOptions();
        options.Validate();

        if (library.Cells.Count == 0)
            throw new InvalidDataException("The GDS library contains no cells.");
        if (!library.Cells.ContainsKey(topCellName))
            throw new InvalidDataException($"GDS top cell '{topCellName}' is not defined in the library.");

        var session = new GdsHierarchyImportSession(library, topCellName, options);
        ct.ThrowIfCancellationRequested();
        var result = options.Mode == GdsHierarchyImportMode.BlackBox
            ? ImportBlackBox(session, topCellName, ct)
            : ImportExploded(session, topCellName, ct);
        return Task.FromResult(result);
    }

    // ── Black-box mode ───────────────────────────────────────────────────────

    private static GdsCircuitImport ImportBlackBox(
        GdsHierarchyImportSession session, string topCellName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var draft = session.BuildBlackBoxDraft(topCellName);
        GdsImportReporter.WarnOnZeroSizeDraft(draft, session.Warnings);
        return new GdsCircuitImport
        {
            Mode = GdsHierarchyImportMode.BlackBox,
            TopCellName = topCellName,
            BoundingBox = session.TopBBox,
            ImportedCellDrafts = [draft],
            Warnings = session.Warnings,
            Infos = session.Infos,
        };
    }

    // ── Explode mode ─────────────────────────────────────────────────────────

    private static GdsCircuitImport ImportExploded(
        GdsHierarchyImportSession session, string topCellName, CancellationToken ct)
    {
        var gdsInstances = session.Flattener.GetInstanceTree(topCellName);
        var drafts = new List<GdsCellDraft>();
        var draftNames = new HashSet<string>();
        var placed = new List<GdsPlacedInstance>();
        var names = new List<string>();
        var pinsPerInstance = new List<IReadOnlyList<GdsAbsolutePin>>();
        var occurrences = new Dictionary<string, int>();
        // One entry per distinct reference transform (an AREF expands to one
        // instance per member — identical warnings must collapse into ONE per
        // reference, with the member count, instead of flooding one per member).
        var transformNotes = new Dictionary<GdsImportReporter.TransformSignature, (string FirstInstance, int Count)>();
        // Skipped cells: export artifacts (note emitted on first encounter) and
        // zero-geometry cells (note emitted after the loop, with the count).
        var artifactNoted = new HashSet<string>(StringComparer.Ordinal);
        var zeroGeometrySkips = new Dictionary<string, int>();
        // Dissolved route/interconnect cells (see GdsRouteCellDissolver): their
        // flattened polygons join the top cell's route polygon sets below so
        // the route matcher reconstructs the connections they draw; one info
        // note per cell (with the instance count) is emitted after the loop.
        var dissolvedWaveguidePolygons = new List<GdsOutlinePolygon>();
        var dissolvedMetalPolygons = new List<GdsOutlinePolygon>();
        var dissolvedRouteCells = new Dictionary<string, int>(StringComparer.Ordinal);

        // Work list: the top cell's direct children, plus — appended while
        // walking — the device instances carried by an expanded export-artifact
        // partial. The flag marks such appended instances: the artifact check
        // still applies to them (artifact-shaped content INSIDE a partial falls
        // back to the skip note), but they are never expanded themselves — the
        // recursion is exactly one level.
        var workList = new List<(GdsInstance Instance, bool FromArtifact)>(gdsInstances.Count);
        foreach (var gdsInstance in gdsInstances)
            workList.Add((gdsInstance, false));

        for (int workIndex = 0; workIndex < workList.Count; workIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var (gdsInstance, fromArtifact) = workList[workIndex];
            string cell = gdsInstance.CellName;

            // Our own export artifacts are not design content of their own — but
            // the mixed-backend nazca partial is written UNFLATTENED: its device
            // cells sit inside as real references with port labels. Recurse ONE
            // level (transforms composed through the partial's own transform) so
            // the devices join the placement set like top-level instances — pin
            // detection and known-component resolution apply as usual. Only the
            // documented shape expands (top → optional 'nazca' wrapper → partial
            // → devices); a flat or deeper-nested partial keeps the skip with
            // ONE note per cell. The match looks through pass-through wrappers:
            // gdsfactory nests a merged partial under nazca's default 'nazca'
            // wrapper, so the direct child is the wrapper, not the artifact cell
            // itself.
            if (GdsExportArtifactExpander.TryUnwrapToArtifact(session.Library, cell, out var artifactLeaf))
            {
                if (!fromArtifact
                    && GdsExportArtifactExpander.TryExpandArtifactInstances(
                        session.Library, gdsInstance, artifactLeaf, out var nested))
                {
                    foreach (var nestedInstance in nested)
                        workList.Add((nestedInstance, true));
                    continue;
                }

                if (artifactNoted.Add(cell))
                {
                    var subject = string.Equals(artifactLeaf, cell, StringComparison.Ordinal)
                        ? $"'{cell}'"
                        : $"'{cell}' (pass-through wrapper for '{artifactLeaf}')";
                    session.Infos.Add(
                        $"Lunima export artifact {subject} skipped — it holds no directly referenced " +
                        "device cells (flattened geometry or deeper nesting), which is not " +
                        "reconstructed (v1).");
                }
                continue;
            }

            var known = session.ResolveKnown(cell);

            // Routed interconnects our exporters emit as REFERENCED cells
            // (instead of flattened top-cell geometry) dissolve into the route
            // polygon sets: the geometry IS a connection, not a component —
            // importing it as a "waveguide" draft would leave the circuit
            // graph disconnected. A deliberate known-component binding wins
            // over dissolution.
            if (known is null
                && GdsRouteCellDissolver.IsRouteCell(cell, session.GetFlattened(cell), session.Options))
            {
                GdsRouteCellDissolver.Dissolve(
                    gdsInstance, session.GetFlattened(cell), session.Options, session.TopBBox,
                    dissolvedWaveguidePolygons, dissolvedMetalPolygons);
                dissolvedRouteCells.TryGetValue(cell, out int dissolved);
                dissolvedRouteCells[cell] = dissolved + 1;
                continue;
            }

            var cellBBox = session.GetCellBBox(cell);

            // Zero-geometry cells (empty flattened bbox — e.g. the zero-length
            // straights gdsfactory's route_bundle inserts): no draft, no
            // instance. They could never be persisted (zero size) or placed, so
            // the per-instance failure cascade collapses into ONE note per cell
            // (after the loop, when the count is known). Connections cannot
            // dangle: dropped instances never enter the abutment matcher, and an
            // unknown zero-geometry cell has no pins to match anyway (the pin
            // detector yields nothing on a degenerate bbox). Cells resolving to
            // a KNOWN component are exempt — the deliberate name binding wins
            // (with the size-mismatch warning covering the geometry gap).
            if (known is null && cellBBox.Width <= 0 && cellBBox.Height <= 0)
            {
                zeroGeometrySkips.TryGetValue(cell, out int skipped);
                zeroGeometrySkips[cell] = skipped + 1;
                continue;
            }

            if (known is null && draftNames.Add(cell))
            {
                var draft = session.BuildDraft(cell);
                GdsImportReporter.WarnOnZeroSizeDraft(draft, session.Warnings);
                drafts.Add(draft);
            }

            // Placement frame for a KNOWN component: pin-anchored when at least
            // one template pin matches a pin label on the cell (the GDS bbox may
            // be inflated by marker paths — benign, it must not shift the
            // placement); the bbox top-left mapping is the fallback and keeps
            // the size-mismatch warning for genuine size/pin mismatches.
            var pinAnchor = known is null ? null : session.GetKnownCellPinAnchor(cell, known, cellBBox);
            if (known is not null && pinAnchor is null)
            {
                session.WarnOnSizeMismatchOnce(cell, known, cellBBox);
            }

            occurrences.TryGetValue(cell, out int occurrence);
            occurrences[cell] = occurrence + 1;
            string instanceName = $"{cell}#{occurrence}";

            double angle = GdsInstancePinProjector.Normalize360(gdsInstance.AngleDegrees);
            double snapped = SnapToCardinal(angle);
            bool nonCardinal = Math.Abs(GdsInstancePinProjector.Normalize180(angle - snapped)) > 1e-9;
            bool magnified = Math.Abs(gdsInstance.Magnification - 1.0) > 1e-9;
            if (nonCardinal || gdsInstance.Reflected || magnified || gdsInstance.Magnification < 0)
            {
                var signature = new GdsImportReporter.TransformSignature(
                    cell, gdsInstance.AngleDegrees, gdsInstance.Reflected, gdsInstance.Magnification);
                transformNotes.TryGetValue(signature, out var note);
                transformNotes[signature] = (note.FirstInstance ?? instanceName, note.Count + 1);
            }

            var topLeft = GdsInstancePinProjector.ProjectPlacedBoundsTopLeft(
                gdsInstance, pinAnchor?.PlacementBox ?? cellBBox, session.TopBBox);
            placed.Add(new GdsPlacedInstance
            {
                InstanceName = instanceName,
                CellName = cell,
                KnownComponentIdentifier = known?.Identifier,
                PdkSource = known?.PdkSource,
                CellDraftName = known is null ? cell : null,
                PositionXUm = topLeft.X,
                PositionYUm = topLeft.Y,
                RotationDegrees = GdsInstancePinProjector.Normalize360(-snapped),
                Reflected = gdsInstance.Reflected,
            });

            // Pin-anchored known components project the TEMPLATE pins shifted by
            // the anchor delta: the authoritative template pin set (names, kinds)
            // at the pins' true GDS positions — the placed component's pins land
            // exactly there (same delta, same transform).
            var cellPins = pinAnchor?.ShiftedPins ?? known?.Pins ?? session.GetCellPins(cell, cellBBox);
            pinsPerInstance.Add(
                GdsInstancePinProjector.ProjectPins(gdsInstance, cellBBox, cellPins, session.TopBBox));
            names.Add(instanceName);
        }

        GdsImportReporter.WarnOnReferenceTransforms(session, transformNotes);

        foreach (var (cell, skipCount) in zeroGeometrySkips)
        {
            session.Infos.Add(
                $"Cell '{cell}' has no geometry (empty bounding box); " +
                $"{skipCount} instance(s) skipped.");
        }

        foreach (var (cell, dissolvedCount) in dissolvedRouteCells)
        {
            session.Infos.Add(
                $"Route cell '{cell}' ({dissolvedCount} instance(s)) dissolved into the top-cell " +
                "routing geometry — reconstructed as connections/route paths instead of a component.");
        }

        if (gdsInstances.Count == 0)
        {
            session.Warnings.Add(
                $"Top cell '{topCellName}' contains no cell references — nothing to explode. " +
                "Use black-box mode to import it as a single component.");
        }

        ct.ThrowIfCancellationRequested();
        var topPorts = session.GetTopLevelPorts()
            .Select(p => new GdsAbsolutePin
            {
                Name = p.Name,
                XUm = p.XUm,
                YUm = p.YUm,
                AngleDegrees = p.AngleDegrees,
                IsElectrical = p.IsElectrical,
            })
            .ToList();

        // Route derivation runs FIRST — waveguide networks, then metal networks
        // over the remaining pins: a top-cell route polygon network drawn
        // between exactly two pins IS the connection (the routing structure
        // tells us the connectivity), so those pins are consumed before the
        // abutment matcher pairs coincident positions — no double-connect.
        // Networks that connect nothing (0/1 pins) or form a junction
        // (>2 pins, noted as info) stay frozen paths on the group.
        var waveguidePolygons = session.GetTopCellWaveguidePolygons()
            .Concat(dissolvedWaveguidePolygons)
            .ToList();
        var metalPolygons = session.GetTopCellMetalPolygons()
            .Concat(dissolvedMetalPolygons)
            .ToList();
        if (session.LabelFallbackUsed && metalPolygons.Count > 0)
        {
            // The fallback firing means this file does not follow our export
            // conventions — then our metal layer NUMBERS are guesses too, and a
            // foundry that assigns them differently gets every optical route
            // reconstructed as electrical (straight metal traces, no bends).
            var metalLayerList = string.Join(", ",
                session.Options.MetalRouteLayers.Select(l => $"({l.Layer},{l.Datatype})"));
            session.Warnings.Add(
                $"Metal-layer defaults {metalLayerList} matched {metalPolygons.Count} top-cell " +
                "polygon(s) in a file whose pin labels needed auto-discovery — if this foundry " +
                "assigns those layers differently, connections import as electrical: correct the " +
                "metal layers in the import dialog.");
        }
        var waveguideRoutes = GdsRouteConnectivityMatcher.Match(
            waveguidePolygons, pinsPerInstance, topPorts,
            session.Options.PinTouchToleranceUm, session.Options.PolygonChainToleranceUm, session.Infos);
        var metalRoutes = GdsRouteConnectivityMatcher.Match(
            metalPolygons, pinsPerInstance, topPorts,
            session.Options.PinTouchToleranceUm, session.Options.PolygonChainToleranceUm, session.Infos,
            electrical: true,
            preConsumedInstancePins: waveguideRoutes.ConsumedInstancePins,
            preConsumedPortIndexes: waveguideRoutes.ConsumedPortIndexes);

        // Metal-derived connections prove the touched pins are electrical (metal
        // only carries electrical signals): infer the domain on the DRAFT pins
        // they touch — geometry-detected pins start kind-unknown, and an
        // unmarked draft pin would be placed as optical and re-route as a
        // waveguide. Known-component pins need no inference (the template kind
        // is authoritative); top-cell ports are not placed at all.
        InferElectricalDraftPins(metalRoutes.Pairs, placed, drafts);

        var frozenRoutePolygons = waveguidePolygons
            .Where((_, index) => !waveguideRoutes.ConsumedPolygonIndexes.Contains(index))
            .Concat(metalPolygons
                .Where((_, index) => !metalRoutes.ConsumedPolygonIndexes.Contains(index)))
            .ToList();
        // Collected BEFORE the accounting report: the residual polygons are the
        // render-only background the report announces (and the collector emits
        // the outline-point-cap drop warning the report deliberately lacks).
        var residualPolygons = session.GetTopCellResidualPolygons();
        GdsImportReporter.ReportTopLevelGeometry(
            session, topCellName, waveguideRoutes, metalRoutes,
            frozenRoutePolygons.Count, residualPolygons.Count,
            dissolvedWaveguidePolygons.Count + dissolvedMetalPolygons.Count);

        var connections = waveguideRoutes.Pairs
            .Concat(metalRoutes.Pairs)
            .Concat(GdsAbutmentMatcher.Match(
                names, pinsPerInstance, topPorts, session.Options.AbutmentToleranceUm, session.Warnings,
                metalRoutes.ConsumedInstancePins, metalRoutes.ConsumedPortIndexes))
            .ToList();

        return new GdsCircuitImport
        {
            Mode = GdsHierarchyImportMode.ExplodeHierarchy,
            TopCellName = topCellName,
            BoundingBox = session.TopBBox,
            ImportedCellDrafts = drafts,
            Instances = placed,
            Connections = connections,
            TopCellWaveguidePolygons = frozenRoutePolygons,
            TopCellResidualPolygons = residualPolygons,
            Warnings = session.Warnings,
            Infos = session.Infos,
        };
    }

    /// <summary>
    /// Marks the DRAFT pins touched by metal-derived connections as electrical
    /// (metal-layer geometry is direct physical evidence of the signal domain).
    /// Only kind-unknown pins (<see cref="DetectedPin.IsElectrical"/> null) are
    /// touched; the change rides the draft into the persisted PDK component, so
    /// the placed component's pin is electrical and the re-created connection
    /// is a metal trace again.
    /// </summary>
    private static void InferElectricalDraftPins(
        IReadOnlyList<GdsPinPair> metalPairs,
        IReadOnlyList<GdsPlacedInstance> placed,
        List<GdsCellDraft> drafts)
    {
        foreach (var pair in metalPairs)
        {
            Infer(pair.A);
            Infer(pair.B);
        }

        void Infer(GdsPinEndpoint endpoint)
        {
            if (endpoint.IsTopLevelPort)
                return;
            var cellDraftName = placed[endpoint.InstanceIndex].CellDraftName;
            if (cellDraftName is null)
                return; // known component — the template's pin kind is authoritative.
            int draftIndex = drafts.FindIndex(d => d.CellName == cellDraftName);
            if (draftIndex < 0)
                return;
            var draftPins = drafts[draftIndex].Pins.ToList();
            int pinIndex = draftPins.FindIndex(p => p.Name == endpoint.PinName && p.IsElectrical is null);
            if (pinIndex < 0)
                return;
            draftPins[pinIndex] = draftPins[pinIndex] with { IsElectrical = true };
            drafts[draftIndex] = drafts[draftIndex] with { Pins = draftPins };
        }
    }

    internal static double SnapToCardinal(double angleDegrees) =>
        GdsInstancePinProjector.Normalize360(
            90.0 * Math.Round(angleDegrees / 90.0, MidpointRounding.AwayFromZero));

    internal static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}

using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Tests for <see cref="GdsHierarchyImporter"/>. Fixtures are built with
/// <see cref="GdsTestWriter"/> (1 db unit = 1 nm, so µm values appear ×1000)
/// and read through <see cref="GdsReader"/> — the same path real files take.
/// Expected coordinates below are in micrometers, app space (Y-down, origin at
/// the top-cell bbox top-left).
/// </summary>
public class GdsHierarchyImporterTests
{
    private const double Tolerance = 1e-6;

    // ── Explode: abutment end-to-end ─────────────────────────────────────────

    [Fact]
    public async Task Explode_TwoAbuttingWaveguides_YieldsDraftsInstancesAndOneConnection()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        result.TopCellName.ShouldBe("TOP");
        result.BoundingBox.MaxX.ShouldBe(20, Tolerance);
        result.BoundingBox.MaxY.ShouldBe(4, Tolerance);
        result.Warnings.ShouldBeEmpty();

        // Two drafts, in order of first appearance.
        result.ImportedCellDrafts.Count.ShouldBe(2);
        var wgA = result.ImportedCellDrafts[0];
        wgA.CellName.ShouldBe("wgA");
        wgA.WidthUm.ShouldBe(10, Tolerance);
        wgA.HeightUm.ShouldBe(4, Tolerance);
        wgA.Pins.Count.ShouldBe(2);
        wgA.Pins[0].Name.ShouldBe("in"); // left edge sorts first
        wgA.Pins[0].XUm.ShouldBe(0, Tolerance);
        wgA.Pins[0].YUm.ShouldBe(2, Tolerance);
        wgA.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        wgA.Pins[1].Name.ShouldBe("out");
        wgA.Pins[1].XUm.ShouldBe(10, Tolerance);
        wgA.Pins[1].AngleDegrees.ShouldBe(0, Tolerance);
        AssertPinsWithinDraftBounds(wgA);
        AssertPinsWithinDraftBounds(result.ImportedCellDrafts[1]);

        // RawCode round-trip snippet with the file-name token: the loaded cell is
        // re-anchored to its bbox bottom-left (the app-space top-left origin), and
        // topcellsonly=False because the imported cell is a SUBcell of TOP.
        wgA.RawCodeBackend.ShouldBe("nazca");
        wgA.RawCode.ShouldContain("def component():");
        wgA.RawCode.ShouldContain("nd.load_gds(filename=\"{GdsFileName}\", cellname=\"wgA\", topcellsonly=False)");
        wgA.RawCode.ShouldContain("_loaded.put(-_bb[0], -_bb[1])");

        // Two instances at app-space positions.
        result.Instances.Count.ShouldBe(2);
        result.Instances[0].InstanceName.ShouldBe("wgA#0");
        result.Instances[0].CellDraftName.ShouldBe("wgA");
        result.Instances[0].KnownComponentIdentifier.ShouldBeNull();
        result.Instances[0].PositionXUm.ShouldBe(0, Tolerance);
        result.Instances[0].PositionYUm.ShouldBe(0, Tolerance);
        result.Instances[0].RotationDegrees.ShouldBe(0, Tolerance);
        result.Instances[0].Reflected.ShouldBeFalse();
        result.Instances[1].PositionXUm.ShouldBe(10, Tolerance);
        result.Instances[1].PositionYUm.ShouldBe(0, Tolerance);

        // Exactly one connection: wgA.out ↔ wgB.in at (10, 2).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    // ── Known-component resolution ───────────────────────────────────────────

    [Fact]
    public async Task Explode_HashSuffixedCell_ResolvesToBaseKnownComponent()
    {
        // A gdsfactory-style hashed cell name resolves to the base PDK name;
        // its pins come from the resolver (authoritative PDK pin names).
        var known = new KnownComponent(
            "mmi1x2", "testpdk", 30, 10,
            new[]
            {
                Pin("o1", 0, 5, 180),
                Pin("o2", 30, 5, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2_A1B2C3", 0, 0)
                .SRef("wgB", 30000, 3000)
            .EndCell()
            .BeginCell("mmi1x2_A1B2C3")
                .Boundary(1, 0, (0, 0), (30000, 0), (30000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "mmi1x2" ? known : null,
            });

        // The known cell produces no draft; only the unknown wgB does.
        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgB");

        result.Instances.Count.ShouldBe(2);
        result.Instances[0].KnownComponentIdentifier.ShouldBe("mmi1x2");
        result.Instances[0].PdkSource.ShouldBe("testpdk");
        result.Instances[0].CellDraftName.ShouldBeNull();
        result.Instances[1].CellDraftName.ShouldBe("wgB");

        // mmi.o2 at GDS (30, 5) abuts wgB.in (offset (30, 3) + cell-local (0, 2)).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("o2");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(30, Tolerance);
        connection.YUm.ShouldBe(5, Tolerance);
        // Resolution visibility: the user sees which library component the cell
        // was bound to — as an INFO note, not a warning (a successful binding
        // is the normal case).
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldHaveSingleItem().ShouldContain("resolved to existing component 'mmi1x2'");
    }

    [Fact]
    public async Task Explode_ExactCellNameMatch_ResolvesWithoutStripping()
    {
        var known = new KnownComponent(
            "wgA", "testpdk", 10, 4,
            new[]
            {
                Pin("in", 0, 2, 180),
                Pin("out", 10, 2, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "wgA" ? known : null,
            });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgB");
        result.Instances[0].KnownComponentIdentifier.ShouldBe("wgA");
        // Connection reconstruction uses the resolver-supplied pins.
        result.Connections.ShouldHaveSingleItem().A.PinName.ShouldBe("out");
        // Resolution visibility: the binding lands in the info-notes channel.
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldHaveSingleItem().ShouldContain("resolved to existing component 'wgA'");
    }

    [Fact]
    public async Task Explode_AmbiguousStrippedNames_NeverGuessed_BecomesDraftWithWarning()
    {
        // Both "thing_AB12" and "thing" resolve to DIFFERENT components: the
        // importer must not guess — the cell becomes a draft.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("thing_AB12_CD34", 0, 0)
            .EndCell()
            .WaveguideCell("thing_AB12_CD34")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name switch
                {
                    "thing_AB12" => new KnownComponent("thingAB", "pdk", 10, 4, Array.Empty<DetectedPin>()),
                    "thing" => new KnownComponent("thingBase", "pdk", 10, 4, Array.Empty<DetectedPin>()),
                    _ => null,
                },
            });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("thing_AB12_CD34");
        result.Instances[0].KnownComponentIdentifier.ShouldBeNull();
        result.Instances[0].CellDraftName.ShouldBe("thing_AB12_CD34");
        result.Warnings.ShouldContain(w => w.Contains("ambiguous") && w.Contains("thing_AB12_CD34"));
    }

    // ── Transforms ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Explode_RotatedInstance90_PinsProjectedNumericallyCorrect()
    {
        // B is rotated 90° CCW (GDS, Y-up) at offset (10, 6) µm. Worked example
        // (all µm): cell "wg" 10×4, pins in=(0,2,180°), out=(10,2,0°).
        // T: x′ = −y + 10, y′ = x + 6. Top bbox = (0,0)-(10,16).
        //   B.in : (0,2) → GDS (8,6)  → app (8, 10), west → down  (90°)
        //   B.out: (10,2) → GDS (8,16) → app (8, 0),  east → up   (270°)
        //   B placed bbox top-left: (6, 0); app rotation = −90° ≡ 270°.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0)
                .SRef("wg", 10000, 6000, angleDegrees: 90)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wg");
        result.Instances.Count.ShouldBe(2);
        result.Instances[0].PositionXUm.ShouldBe(0, Tolerance);
        result.Instances[0].PositionYUm.ShouldBe(12, Tolerance); // 16 − 4: A sits at the bottom
        result.Instances[1].PositionXUm.ShouldBe(6, Tolerance);
        result.Instances[1].PositionYUm.ShouldBe(0, Tolerance);
        result.Instances[1].RotationDegrees.ShouldBe(270, Tolerance); // GDS +90° ≡ app −90°
        result.Connections.ShouldBeEmpty();

        // Numeric projection check of the rotated pins through the internal projector.
        var flattener = new GdsCellFlattener(library);
        var gdsInstances = flattener.GetInstanceTree("TOP");
        var cellBBox = flattener.GetBoundingBox("wg");
        var topBBox = flattener.GetBoundingBox("TOP");
        var pins = new[] { Pin("in", 0, 2, 180), Pin("out", 10, 2, 0) };

        var projected = GdsInstancePinProjector.ProjectPins(gdsInstances[1], cellBBox, pins, topBBox);
        projected[0].Name.ShouldBe("in");
        projected[0].XUm.ShouldBe(8, Tolerance);
        projected[0].YUm.ShouldBe(10, Tolerance);
        projected[0].AngleDegrees.ShouldBe(90, Tolerance);
        projected[1].Name.ShouldBe("out");
        projected[1].XUm.ShouldBe(8, Tolerance);
        projected[1].YUm.ShouldBe(0, Tolerance);
        projected[1].AngleDegrees.ShouldBe(270, Tolerance);
    }

    [Fact]
    public async Task Explode_ReflectedInstance_WarnsAndReconstructionUsesReflectedTransform()
    {
        // Asymmetric pin: "in" at cell GDS (0, 3). Mirrored about the cell's X
        // axis it lands at GDS (0, −3); the top bbox becomes (0,−4)-(10,0), so
        // app Y = 0 − (−3) = 3 — an unreflected reconstruction would say 1.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, reflected: true)
            .EndCell()
            .WaveguideCell("wg", inY: 3000)
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var instance = result.Instances.ShouldHaveSingleItem();
        instance.Reflected.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("mirrored") && w.Contains("unreflected"));
        result.Infos.ShouldBeEmpty("transform caveats stay warnings — nothing informational here");

        var flattener = new GdsCellFlattener(library);
        var projected = GdsInstancePinProjector.ProjectPins(
            flattener.GetInstanceTree("TOP")[0],
            flattener.GetBoundingBox("wg"),
            new[] { Pin("in", 0, 1, 180), Pin("out", 10, 2, 0) },
            flattener.GetBoundingBox("TOP"));

        // Note: the cell-local app pin (0,1) — app frame of the UNREFLECTED
        // cell — is what gets projected through the true reflected transform.
        projected[0].XUm.ShouldBe(0, Tolerance);
        projected[0].YUm.ShouldBe(3, Tolerance);
        projected[0].AngleDegrees.ShouldBe(180, Tolerance); // X-mirror keeps horizontal directions
        projected[1].XUm.ShouldBe(10, Tolerance);
        projected[1].YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public async Task Explode_NonCardinalAngle_SnappedToNearestCardinalWithWarning()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, angleDegrees: 45)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Instances.ShouldHaveSingleItem().RotationDegrees.ShouldBe(270, Tolerance); // 45° → 90° → app −90°
        result.Warnings.ShouldContain(w =>
            w.Contains("45") && w.Contains("90") && w.Contains("Manhattan"));
        result.Infos.ShouldBeEmpty("the rotation snap is a caveat, not an informational note");
    }

    // ── Abutment edge cases ──────────────────────────────────────────────────

    [Fact]
    public async Task Explode_AmbiguousPinPartners_WarnsAndFirstMatchWins()
    {
        // src.out coincides with BOTH sink instances' "in" pins (30 nm apart,
        // within the 0.05 µm default tolerance).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("src", 0, 0)
                .SRef("sink", 10000, 0)
                .SRef("sink", 10000, 30)
            .EndCell()
            .WaveguideCell("src")
            .WaveguideCell("sink")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1); // first sink in placement order wins
        connection.B.PinName.ShouldBe("in");
        result.Warnings.ShouldContain(w => w.Contains("candidates") && w.Contains("src#0"));
    }

    [Fact]
    public async Task Explode_TopLevelLabels_BecomeExternalPortConnections()
    {
        // Circuit ports as top-level labels; the label at the internal abutment
        // must NOT steal the instance-to-instance connection (instances win).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
                .Text(1, 10, "in0", 0, 2000)
                .Text(1, 10, "mid", 10000, 2000)
                .Text(1, 10, "out0", 20000, 2000)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Connections.Count.ShouldBe(3);

        var abutment = result.Connections.Where(c => c.A.PinName == "out" && c.B.PinName == "in").ShouldHaveSingleItem();
        abutment.A.InstanceIndex.ShouldBe(0);
        abutment.B.InstanceIndex.ShouldBe(1);

        var input = result.Connections.Where(c => c.B.PinName == "in0").ShouldHaveSingleItem();
        input.A.InstanceIndex.ShouldBe(0);
        input.A.PinName.ShouldBe("in");
        input.B.IsTopLevelPort.ShouldBeTrue();
        input.XUm.ShouldBe(0, Tolerance);
        input.YUm.ShouldBe(2, Tolerance);

        var output = result.Connections.Where(c => c.B.PinName == "out0").ShouldHaveSingleItem();
        output.A.InstanceIndex.ShouldBe(1);
        output.A.PinName.ShouldBe("out");
        output.B.IsTopLevelPort.ShouldBeTrue();

        // The "mid" port lost to the instance-to-instance pair (one partner per pin).
        result.Connections.ShouldNotContain(c => c.B.PinName == "mid");
    }

    // ── Black-box mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task BlackBox_TopCell_BecomesSingleDraftWithoutInstances()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 10000, 0)
                .Text(1, 10, "a0", 0, 2000)
                .Text(1, 10, "a1", 20000, 2000)
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        result.Mode.ShouldBe(GdsHierarchyImportMode.BlackBox);
        result.Instances.ShouldBeEmpty();
        result.Connections.ShouldBeEmpty();

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.CellName.ShouldBe("TOP");
        draft.WidthUm.ShouldBe(20, Tolerance);
        draft.HeightUm.ShouldBe(4, Tolerance);

        // Black-box pins come from the WHOLE flattened hierarchy: the top cell's
        // own labels keep their bare names (they are the circuit's ports), the
        // absorbed children's labels are promoted with their cell context
        // (each of wgA/wgB occurs once, so no occurrence qualifier). Detector
        // order: left edge top-down, then top edge, then right edge.
        draft.Pins.Select(p => p.Name).ShouldBe(new[]
        {
            "wgA_in", "a0", "wgA_out", "wgB_in", "wgB_out", "a1",
        });
        AssertPinsWithinDraftBounds(draft);

        // Outlines absorb the whole hierarchy (stripe + extent per child).
        draft.Outlines.Count.ShouldBe(4);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task BlackBox_NestedLabels_InferDirectionAndKindFromLocalGeometry()
    {
        // The user's black-box case: a 100 × 80 µm top cell absorbing one device
        // cell whose pins sit DEEP inside the bbox. The metal pad pin must point
        // along the pad's end face (the bbox edge rule would point it at the
        // outer left edge) and read electrical from the metal layer alone ("m1"
        // matches no electrical name); the waveguide pin stays optical; the top
        // cell's own "anode" label has no geometry near and falls back to the
        // bbox direction plus the name-based kind. The fixture is a FOREIGN
        // file (no Lunima sentinel), so its metal layer is supplied as an
        // explicit mapping — the (11, 0) default no longer applies here.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 20000, 30000)
                .Boundary(111, 0, (0, 0), (100000, 0), (100000, 80000), (0, 80000), (0, 0))
                .Text(1, 10, "anode", 50000, 50000)
            .EndCell()
            .BeginCell("dev")
                .Boundary(11, 0, (6000, 2750), (10000, 2750), (10000, 3250), (6000, 3250), (6000, 2750))
                .Boundary(1, 0, (0, 750), (4000, 750), (4000, 1250), (0, 1250), (0, 750))
                .Text(1, 10, "m1", 10000, 3000)
                .Text(1, 10, "o1", 0, 1000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                Mode = GdsHierarchyImportMode.BlackBox,
                PinDetection = new GdsPinDetectionOptions { ElectricalLayers = [(11, 0)] },
            });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        result.Warnings.ShouldBeEmpty();
        draft.Pins.Count.ShouldBe(3);

        // dev_m1: on the pad's right end face → east (the bbox rule would say
        // 180°, left edge); electrical from the metal polygon touch.
        var metal = draft.Pins.Single(p => p.Name == "dev_m1");
        metal.XUm.ShouldBe(30, Tolerance);
        metal.YUm.ShouldBe(47, Tolerance);
        metal.AngleDegrees.ShouldBe(0, Tolerance);
        metal.IsElectrical.ShouldBe(true);

        // dev_o1: on the waveguide's left end face → west, kind stays unknown
        // (optical downstream).
        var optical = draft.Pins.Single(p => p.Name == "dev_o1");
        optical.XUm.ShouldBe(20, Tolerance);
        optical.YUm.ShouldBe(49, Tolerance);
        optical.AngleDegrees.ShouldBe(180, Tolerance);
        optical.IsElectrical.ShouldBeNull();

        // anode: no geometry near → bbox fallback direction (top edge), kind
        // from the name.
        var named = draft.Pins.Single(p => p.Name == "anode");
        named.XUm.ShouldBe(50, Tolerance);
        named.YUm.ShouldBe(30, Tolerance);
        named.AngleDegrees.ShouldBe(270, Tolerance);
        named.IsElectrical.ShouldBe(true);

        AssertPinsWithinDraftBounds(draft);
    }

    // ── Outlines ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BlackBox_Outline_SimplifiedUnderPointCap_LayerKept_YDown()
    {
        // 72-gon "circle" (radius 5 µm) on layer 3 plus a bbox rectangle on
        // layer 1; point cap 25 forces adaptive tolerance growth.
        var circlePoints = Enumerable.Range(0, 72)
            .Select(i =>
            {
                double angle = 2 * Math.PI * i / 72;
                return ((int)Math.Round(5000 + 5000 * Math.Cos(angle)),
                        (int)Math.Round(5000 + 5000 * Math.Sin(angle)));
            })
            .Append((10000, 5000)) // close the ring
            .ToArray();

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(3, 0, circlePoints)
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                Mode = GdsHierarchyImportMode.BlackBox,
                MaxOutlinePointsPerCell = 25,
            });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Outlines.Count.ShouldBe(2);
        draft.Outlines.Sum(p => p.Points.Count).ShouldBeLessThanOrEqualTo(25);

        // Layer/datatype survive simplification.
        draft.Outlines.ShouldContain(p => p.Layer == 3 && p.DataType == 0);
        draft.Outlines.ShouldContain(p => p.Layer == 1 && p.DataType == 0);

        // App-space Y-down: the rectangle's top edge (GDS MaxY) maps to y = 0.
        var rectangle = draft.Outlines.First(p => p.Layer == 1);
        rectangle.Points.ShouldContain(pt => pt.Y == 0 && pt.X == 0);
        rectangle.Points.ShouldContain(pt => pt.Y == 0 && pt.X == 10);
        rectangle.Points.Min(pt => pt.Y).ShouldBe(0, Tolerance);
        rectangle.Points.Max(pt => pt.Y).ShouldBe(10, Tolerance);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_UnknownTopCell_ThrowsInvalidData()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP").EndCell()
            .EndLibrary()
            .ToArray());

        await Should.ThrowAsync<InvalidDataException>(
            () => GdsHierarchyImporter.ImportAsync(library, "MISSING", new GdsHierarchyImportOptions()));
    }

    [Fact]
    public async Task ImportAsync_EmptyLibrary_ThrowsInvalidData()
    {
        await Should.ThrowAsync<InvalidDataException>(
            () => GdsHierarchyImporter.ImportAsync(new GdsLibrary(), "TOP", new GdsHierarchyImportOptions()));
    }

    [Fact]
    public async Task Explode_TopCellWithoutInstances_WarnsNothingToExplode()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldBeEmpty();
        result.Instances.ShouldBeEmpty();
        result.Connections.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("nothing to explode"));
        // The lone (1,0) polygon is top-cell own geometry on a waveguide layer:
        // it comes back as a frozen path — an INFO, not a warning (nothing is
        // silently dropped: the frozen path stays visible on the group).
        result.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        var polygon = result.TopCellWaveguidePolygons.ShouldHaveSingleItem();
        polygon.Layer.ShouldBe(1);
        polygon.Points.Select(p => (p.X, p.Y)).ShouldBe(new[]
        {
            (0.0, 4.0), (10.0, 4.0), (10.0, 0.0), (0.0, 0.0), (0.0, 4.0),
        });
    }

    // ── Top-cell own waveguide polygons (frozen route paths) ─────────────────

    [Fact]
    public async Task Explode_TopCellOwnWaveguidePolygon_ImportedWithPinnedCoordinates()
    {
        // The route polygon spanning the 5 µm gap between wgA (ends at x=10) and
        // wgB (starts at x=15) is the top cell's OWN geometry on the waveguide
        // layer — it must import, in app space (Y-down, origin at the top bbox
        // top-left (0, 4)). The wgA/wgB core stripes sit on the same (1,0) layer
        // but belong to the INSTANCES — importing them here too would double-draw
        // every component's waveguide, so exactly ONE polygon comes back.
        //
        // The polygon runs at app y ∈ [3.25, 3.75] (GDS y 250…750), 1.25 µm clear
        // of the pin line: the pin labels sit at GDS y=2000 → app y=2 (wgA.out at
        // (10, 2), wgB.in at (15, 2)), and the route-connectivity touch tolerance
        // is 1.0 µm — 1.25 µm of clearance keeps the polygon pin-less, so it
        // stays a FROZEN PATH instead of being consumed as a connection.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 15000, 0)
                .Boundary(1, 0, (10000, 250), (15000, 250), (15000, 750), (10000, 750), (10000, 250))
                .Boundary(68, 0, (0, 0), (25000, 0), (25000, 4000), (0, 4000), (0, 0)) // devrec halo
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var polygon = result.TopCellWaveguidePolygons.ShouldHaveSingleItem(
            "only the top cell's OWN (1,0) polygon — instance geometry stays out");
        polygon.Layer.ShouldBe(1);
        polygon.DataType.ShouldBe(0);
        polygon.Points.Select(p => (p.X, p.Y)).ShouldBe(new[]
        {
            (10.0, 3.75), (15.0, 3.75), (15.0, 3.25), (10.0, 3.25), (10.0, 3.75),
        });

        // Restored/frozen accounting is INFO (fully reconstructed geometry);
        // the devrec halo — on neither the route nor the metal layers — comes
        // back as render-only background geometry (also INFO, not a warning).
        result.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        result.Infos.ShouldContain(i => i.Contains("render-only background geometry"));
        result.Warnings.ShouldBeEmpty();
        var residual = result.TopCellResidualPolygons.ShouldHaveSingleItem();
        residual.Layer.ShouldBe(68);
        residual.Points.Select(p => (p.X, p.Y)).ShouldBe(new[]
        {
            (0.0, 4.0), (25.0, 4.0), (25.0, 0.0), (0.0, 0.0), (0.0, 4.0),
        });
    }

    [Fact]
    public async Task Explode_TopCellOwnWaveguidePolygon_TouchingTwoPins_BecomesRouteDerivedConnection()
    {
        // The SAME geometry as the frozen-path test, but with the polygon ON the
        // pin line: it bridges the 5 µm gap between wgA.out (app (10, 2)) and
        // wgB.in (app (15, 2)) — its end edges pass exactly through both pins.
        // The drawn route IS the connectivity, so the polygon is consumed into a
        // real (re-routable) connection instead of coming back as a frozen path.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 15000, 0)
                .Boundary(1, 0, (10000, 1750), (15000, 1750), (15000, 2250), (10000, 2250), (10000, 1750))
                .Boundary(68, 0, (0, 0), (25000, 0), (25000, 4000), (0, 4000), (0, 0)) // devrec halo
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellWaveguidePolygons.ShouldBeEmpty(
            "the bridging polygon was consumed into a route-derived connection");
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(12.5, Tolerance);
        connection.YUm.ShouldBe(2.0, Tolerance);

        // The restored connection is INFO (normal, fully-reconstructed
        // behavior); the devrec halo comes back as background geometry (INFO).
        result.Infos.ShouldContain(i => i.Contains("restored as 1 real connection(s) (re-routable)"));
        result.Infos.ShouldContain(i => i.Contains("render-only background geometry"));
        result.Warnings.ShouldBeEmpty();
        result.TopCellResidualPolygons.ShouldHaveSingleItem().Layer.ShouldBe(68);
    }

    [Fact]
    public async Task Explode_TopCellOwnWaveguidePath_TouchingTwoPins_BecomesRouteDerivedConnection()
    {
        // The same bridge as the polygon test above, but drawn the way real
        // PDK exports draw most routing: a PATH (centerline + width) instead
        // of a BOUNDARY. The 0.5 µm wide centerline runs on the pin line from
        // wgA.out (app (10, 2)) to wgB.in (app (15, 2)); its outline quad is
        // geometrically identical to the polygon fixture, so the route matcher
        // must consume it into the same route-derived connection.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("wgB", 15000, 0)
                .Path(1, 0, widthDbUnits: 500, pathType: 0, (10000, 2000), (15000, 2000))
                .Boundary(68, 0, (0, 0), (25000, 0), (25000, 4000), (0, 4000), (0, 0)) // devrec halo
            .EndCell()
            .WaveguideCell("wgA")
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellWaveguidePolygons.ShouldBeEmpty(
            "the path's outline quad was consumed into a route-derived connection");
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(12.5, Tolerance);
        connection.YUm.ShouldBe(2.0, Tolerance);

        result.Infos.ShouldContain(i => i.Contains("restored as 1 real connection(s) (re-routable)"));
        result.Infos.ShouldContain(i => i.Contains("render-only background geometry"));
        result.Warnings.ShouldBeEmpty();
        result.TopCellResidualPolygons.ShouldHaveSingleItem().Layer.ShouldBe(68);
    }

    [Fact]
    public async Task Explode_TopCellOwnGeometryOnlyOnNonWaveguideLayers_BecomesBackgroundGeometry()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .Boundary(68, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0)) // devrec only
            .EndCell()
            .WaveguideCell("wgA")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellWaveguidePolygons.ShouldBeEmpty("a devrec halo is not routing geometry");
        // The halo is not lost: it rides the created group as render-only
        // background geometry — an INFO, not the stale "not reconstructed" warning.
        result.Infos.ShouldContain(i =>
            i.Contains("1 polygon(s) on other layers are imported as render-only background geometry"));
        result.Warnings.ShouldBeEmpty();
        result.TopCellResidualPolygons.ShouldHaveSingleItem().Layer.ShouldBe(68);
    }

    [Fact]
    public async Task Explode_NazcaDefaultInterconnectPolygon_ImportedByDefault()
    {
        // Our OWN exporter flattens routed connections with nazca's default
        // interconnect (nd.strt/nd.bend), which writes polygons on layer
        // (1111,0) — a re-import must recognize it without any option tuning.
        // The polygon is drawn OFF the pins (1.5 µm clear of the 1 µm touch
        // tolerance): a polygon touching exactly two pins of ONE instance is a
        // feedback loop and becomes a route-derived connection instead of a
        // frozen path — not what this layer-recognition test is about.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .Boundary(1111, 0, (0, 3500), (10000, 3500), (10000, 4000), (0, 4000), (0, 3500))
            .EndCell()
            .WaveguideCell("wgA")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellWaveguidePolygons.ShouldHaveSingleItem().Layer.ShouldBe(1111);
        result.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        result.Warnings.ShouldBeEmpty("the (1111,0) polygon is fully reconstructed as a frozen path — no leftover geometry");
    }

    [Fact]
    public async Task Explode_CustomRouteLayers_ImportsOnlyTheConfiguredLayer()
    {
        // The route-layer list is configurable (GdsHierarchyImportOptions.RouteLayers);
        // with (3,0) configured the (1,0) polygon is NOT routing anymore.
        // The (3,0) polygon is drawn 1.5 µm clear of the pins (touch tolerance
        // is 1 µm): touching exactly two pins of ONE instance would make it a
        // route-derived feedback-loop connection, not the frozen path this
        // layer-filtering test asserts.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .Boundary(3, 0, (0, 0), (10000, 0), (10000, 500), (0, 500), (0, 0))
                .Boundary(1, 0, (0, 3000), (10000, 3000), (10000, 3500), (0, 3500), (0, 3000))
            .EndCell()
            .WaveguideCell("wgA")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions
        {
            RouteLayers = [(3, 0)],
        });

        var polygon = result.TopCellWaveguidePolygons.ShouldHaveSingleItem();
        polygon.Layer.ShouldBe(3);
        polygon.Points.Select(p => (p.X, p.Y)).ShouldBe(new[]
        {
            (0.0, 4.0), (10.0, 4.0), (10.0, 3.5), (0.0, 3.5), (0.0, 4.0),
        });
        // The (3,0) polygon comes back as a frozen path (INFO); the (1,0)
        // polygon is on no configured route layer — render-only background (INFO).
        result.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        result.Infos.ShouldContain(i => i.Contains("render-only background geometry"));
        result.Warnings.ShouldBeEmpty();
        result.TopCellResidualPolygons.ShouldHaveSingleItem().Layer.ShouldBe(1);
    }

    [Fact]
    public async Task Explode_ResidualPolygonsOverOutlineCap_DroppedWithTrueCountWarning()
    {
        // 2500 tiny (68,0) squares = 12500 outline points against the 8000-point
        // cap. A square never simplifies below its 5 ring points (and must not
        // silently vanish when the escalated tolerance overshoots it), so the cap
        // is enforced by counted drops only: 1600 × 5 = 8000 points exactly fill
        // the cap, 900 are dropped, and the warning reports that true count.
        var writer = GdsTestWriter.Create().StandardPrologue().BeginCell("TOP");
        for (int i = 0; i < 2500; i++)
        {
            int x = (i % 50) * 1000;
            int y = (i / 50) * 1000;
            writer.Boundary(68, 0, (x, y), (x + 500, y), (x + 500, y + 500), (x, y + 500), (x, y));
        }
        var library = await ReadLibraryAsync(writer.EndCell().EndLibrary().ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.TopCellResidualPolygons.Count.ShouldBe(1600,
            "1600 squares × 5 points exactly fill the 8000-point cap");
        result.Warnings.ShouldContain(w => w.Contains("dropped 900 background polygon(s)"),
            "the warning reports the true dropped count (2500 − 1600 kept)");
        result.Infos.ShouldContain(i => i.Contains("render-only background geometry"));
    }

    // ── Zero-geometry / export-artifact cells ────────────────────────────────

    [Fact]
    public async Task Explode_ZeroGeometryCell_DroppedWithOneInfoNote_InstancesExcluded()
    {
        // "zeroL" mimics a gdsfactory zero-length straight (a route_bundle
        // artifact): its only content is a port label at a single point, so the
        // flattened bbox is empty (0 × 0). Two instances of it sit exactly on
        // wgA's pins — the wgA.out ↔ wgB.in abutment must survive untouched.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("zeroL", 0, 2000)
                .SRef("zeroL", 10000, 2000)
                .SRef("wgB", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("zeroL")
                .Text(1, 10, "io", 0, 0)
            .EndCell()
            .WaveguideCell("wgB")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        // The normal cells import as before; the zero-geometry cell leaves no
        // draft and no instances behind.
        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "wgB" });
        result.Instances.Count.ShouldBe(2);
        result.Instances.ShouldNotContain(i => i.CellName == "zeroL");

        // The old cascade (empty-bbox warning, "not registered: zero size",
        // per-instance placement skips) collapses into ONE info note per cell.
        result.Warnings.ShouldBeEmpty();
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("'zeroL'");
        note.ShouldContain("2 instance(s) skipped");

        // The surviving connection is the wgA.out ↔ wgB.in abutment — nothing
        // references the dropped instances (they never entered the matcher).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public async Task Explode_LunimaExportArtifactCell_SkippedWithOneInfoNote()
    {
        // 'ConnectAPIC_NazcaPartial' is the top cell name our mixed-backend
        // export (MixedBackendGdsOrchestrator.NazcaPartialTopCellName) writes
        // for the flattened nazca partial. It HAS geometry (on a non-port
        // layer, hence pinless) — the skip is by name convention, independent
        // of the zero-geometry drop. Two references still yield ONE note.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("ConnectAPIC_NazcaPartial", 20000, 0)
                .SRef("ConnectAPIC_NazcaPartial", 40000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Warnings.ShouldBeEmpty(
            "the artifact is skipped by convention, not failed as an unpersistable draft");
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("ConnectAPIC_NazcaPartial");
        note.ShouldContain("export artifact");
        note.ShouldContain("not reconstructed");
    }

    [Fact]
    public async Task Explode_LunimaExportArtifactBehindPassThroughWrapper_SkippedWithOneInfoNote()
    {
        // The user's mixed-backend round-trip: gdsfactory's import_gds names the
        // merged component after the source file's TOP cell, so the flattened
        // nazca partial re-enters the merged file behind nazca's default 'nazca'
        // pass-through wrapper (one untransformed reference, nothing else). A
        // name-only artifact check misses this — the wrapper became a pin-less
        // draft and failed with "not registered: no pins" plus a skipped
        // placement, the exact cascade the artifact skip exists to prevent.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("nazca", 20000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 0, 0)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Warnings.ShouldBeEmpty(
            "the wrapped artifact is skipped by convention, not failed as an unpersistable draft");
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("nazca");
        note.ShouldContain("ConnectAPIC_NazcaPartial");
        note.ShouldContain("export artifact");
        note.ShouldContain("not reconstructed");
    }

    [Fact]
    public async Task Explode_PassThroughWrapperAroundNormalCell_IsNotSkipped()
    {
        // The wrapper look-through must ONLY recognize wrappers around artifact
        // cells: a pass-through wrapper around a real design cell imports
        // exactly as before (draft named after the wrapper, absorbing the
        // subtree — pins included).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("nazca", 0, 0)
            .EndCell()
            .BeginCell("nazca")
                .SRef("wgA", 0, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.ImportedCellDrafts[0].Pins.Count.ShouldBe(2);
        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldBeEmpty("a wrapper around a normal cell is not an export artifact");
    }

    [Fact]
    public async Task Explode_TransformedWrapperAroundArtifact_IsNotSkipped()
    {
        // A wrapper whose reference TRANSFORMS the artifact (here: 90° rotation)
        // is not a pass-through — unwrapping would misplace the geometry, so the
        // cell is treated as ordinary design content (draft, no artifact note).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("nazca", 0, 0)
            .EndCell()
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 0, 0, angleDegrees: 90)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 3000), (0, 3000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("nazca");
        result.Infos.ShouldBeEmpty("a transforming wrapper is not a pass-through artifact");
    }

    [Fact]
    public async Task Explode_ArtifactPartialWithDeviceReferences_DevicesJoinPlacementSet()
    {
        // The mixed-backend reality: the partial is NOT flattened — its device
        // cells are real references with port labels. The import recurses ONE
        // level: the devices join the placement set as if they were top-level
        // instances (transforms composed through the partial's own transform),
        // pin detection applies, and the old skip note disappears. The AREF
        // inside the partial expands per member.
        // dev: 5×4 µm, built like WaveguideCell — a (1,0) core stripe inside a
        // (111,0) extent rectangle (which sizes the bbox without firing the
        // edge heuristic), labels opt1/opt2 on the left/right edge midpoints.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("ConnectAPIC_NazcaPartial", 10000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("ConnectAPIC_NazcaPartial")
                .SRef("dev", 0, 0)
                .ARef("dev", columns: 2, rows: 1, originX: 20000, originY: 0,
                    columnSpacingDbUnits: 10000, rowSpacingDbUnits: 5000)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (5000, 1750), (5000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "opt1", 0, 2000)
                .Text(1, 10, "opt2", 5000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        // One draft per distinct cell: wgA and dev — never the partial itself.
        result.ImportedCellDrafts.Select(d => d.CellName).ShouldBe(new[] { "wgA", "dev" });
        var dev = result.ImportedCellDrafts[1];
        dev.WidthUm.ShouldBe(5, Tolerance);
        dev.HeightUm.ShouldBe(4, Tolerance);
        dev.Pins.Select(p => p.Name).ShouldBe(new[] { "opt1", "opt2" });

        // The partial's three device references (one SREF + two AREF members)
        // are placed at their composed offsets: partial (10, 0) + child offset.
        // Top bbox: MinX 0, MaxY 4 (all cells share the 4 µm extent) → the
        // placed bbox top-left of a dev at origin (x, 0) is app (x, 0).
        result.Instances.Select(i => i.InstanceName).ShouldBe(
            new[] { "wgA#0", "dev#0", "dev#1", "dev#2" });
        var dev0 = result.Instances[1];
        dev0.CellDraftName.ShouldBe("dev");
        dev0.PositionXUm.ShouldBe(10, Tolerance);
        dev0.PositionYUm.ShouldBe(0, Tolerance);
        dev0.RotationDegrees.ShouldBe(0, Tolerance);
        result.Instances[2].PositionXUm.ShouldBe(30, Tolerance);
        result.Instances[3].PositionXUm.ShouldBe(40, Tolerance);

        result.Warnings.ShouldBeEmpty();
        result.Infos.ShouldBeEmpty("an expanded partial produces no artifact-skip note");

        // wgA.out (10, 2) coincides with dev#0.opt1 — the device pin projected
        // through the composed transform takes part in abutment matching.
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("opt1");
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public async Task Explode_WrappedArtifactPartial_TransformsComposeThroughWrapperChain()
    {
        // The documented mixed-backend shape: top → 'nazca' pass-through
        // wrapper → partial → devices. Offsets on every level compose (the
        // wrapper instance at (6, 0), the wrapper→partial reference at (3, 0),
        // the partial→dev reference at (1, 0) → dev origin (10, 0) GDS).
        // The device resolves to a KNOWN component by name — the resolver
        // applies to expanded devices exactly like to top-level instances.
        var known = new KnownComponent(
            "dev", "testpdk", 5, 4,
            new[] { Pin("opt1", 0, 2, 180), Pin("opt2", 5, 2, 0) });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("nazca", 6000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 3000, 0)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .SRef("dev", 1000, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (5000, 1750), (5000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "opt1", 0, 2000)
                .Text(1, 10, "opt2", 5000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "dev" ? known : null,
            });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.Select(i => i.InstanceName).ShouldBe(new[] { "wgA#0", "dev#0" });
        var dev = result.Instances[1];
        dev.KnownComponentIdentifier.ShouldBe("dev");
        dev.CellDraftName.ShouldBeNull();
        dev.PositionXUm.ShouldBe(10, Tolerance);
        dev.PositionYUm.ShouldBe(0, Tolerance);

        result.Warnings.ShouldBeEmpty();
        // The only note is the known-component resolution — no artifact skip.
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("resolved to existing component 'dev'");

        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.PinName.ShouldBe("out");
        connection.B.PinName.ShouldBe("opt1");
    }

    [Fact]
    public async Task Explode_ArtifactBehindTwoWrapperLevels_FallsBackToSkipNote()
    {
        // Deeper than the documented shape (top → wrapper → partial): TWO
        // wrapper levels between the top cell and the partial. The unwrap still
        // recognizes the artifact (the look-through walks pass-through chains),
        // but the recursion supports one wrapper level only — the cell keeps
        // the skip + info note and its devices do not import.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wgA", 0, 0)
                .SRef("outer", 20000, 0)
            .EndCell()
            .WaveguideCell("wgA")
            .BeginCell("outer")
                .SRef("nazca", 0, 0)
            .EndCell()
            .BeginCell("nazca")
                .SRef("ConnectAPIC_NazcaPartial", 0, 0)
            .EndCell()
            .BeginCell("ConnectAPIC_NazcaPartial")
                .SRef("dev", 1000, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (5000, 1750), (5000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (5000, 0), (5000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "opt1", 0, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Instances.ShouldHaveSingleItem().CellName.ShouldBe("wgA");
        result.Warnings.ShouldBeEmpty();
        var note = result.Infos.ShouldHaveSingleItem();
        note.ShouldContain("export artifact");
        note.ShouldContain("'outer'");
        note.ShouldContain("ConnectAPIC_NazcaPartial");
        note.ShouldContain("not reconstructed");
    }

    [Fact]
    public async Task Explode_ZeroGeometryCellResolvingToKnownComponent_IsKept()
    {
        // A deliberate name binding to a PDK component wins over the
        // zero-geometry drop (the size-mismatch warning covers the geometry
        // gap) — only UNKNOWN zero-geometry cells are dropped.
        var known = new KnownComponent(
            "anchor", "testpdk", 5, 5,
            new[] { Pin("io", 0, 0, 180) });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("anchor", 0, 0)
            .EndCell()
            .BeginCell("anchor")
                .Text(1, 10, "io", 0, 0)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "anchor" ? known : null,
            });

        result.Instances.ShouldHaveSingleItem().KnownComponentIdentifier.ShouldBe("anchor");
        result.Warnings.ShouldContain(w => w.Contains("UNSCALED"),
            "the known component keeps its size-mismatch warning");
        result.Infos.ShouldNotContain(i => i.Contains("skipped"),
            "a resolved zero-geometry cell is not treated as a skip");
    }

    // ── Pin-name normalization (blank/duplicate names) ───────────────────────

    [Fact]
    public async Task Explode_DuplicateAndHeuristicCollidingPinNames_DedupedWithWarnings()
    {
        // Two labels with the same text (legal GDS) plus a label literally
        // named "heur_1" colliding with the heuristic pin: names are made
        // unique BEFORE connection reconstruction, so pin-by-name resolution
        // can never mis-wire. Pin order: left edge by app Y (label y=0,
        // heuristic y=500, label y=1000), then the right-edge label.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dup", 0, 0)
            .EndCell()
            .BeginCell("dup")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "o1", 0, 1500)
                .Text(1, 10, "o1", 0, 2500)        // same text twice — legal GDS
                .Text(1, 10, "heur_1", 10000, 2000) // collides with the heuristic pin name
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Select(p => p.Name).ShouldBe(new[] { "o1", "heur_1", "o1_2", "heur_1_2" });
        result.Warnings.ShouldContain(w => w.Contains("duplicate pin name 'o1'") && w.Contains("o1_2"));
        result.Warnings.ShouldContain(w => w.Contains("duplicate pin name 'heur_1'") && w.Contains("heur_1_2"));
    }

    // ── Multi-line metadata labels ───────────────────────────────────────────

    [Fact]
    public async Task Explode_MultiLineMetadataLabelOnPortLayer_DoesNotBecomeDraftPin()
    {
        // Foundry files carry metadata blobs as TEXT records — nazca writes
        // "cellname: …\nfoundry_pdk: …" into every cell. Even on the configured
        // port layer such a blob is not a port label: a pin name cannot span
        // lines (the top-level-port path already filters these).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
                .Text(1, 10, "cellname: dev\nfoundry_pdk: test_pdk", 5000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Select(p => p.Name).ShouldBe(new[] { "in", "out" },
            "the multi-line metadata blob must not become a pin");
        draft.Pins.ShouldNotContain(p => p.Name.Contains('\n'));
    }

    [Fact]
    public async Task Explode_MultiLineMetadataLabelOnOddLayer_FallbackIgnoresIt()
    {
        // The any-layer label fallback sweeps EVERY text when no configured port
        // layer yields a label — without the multi-line filter it promoted the
        // metadata blob to a pin with embedded newlines in its name (seen on a
        // real production file).
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(66, 0, "cellname: dev\nfoundry_pdk: test_pdk", 5000, 1000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.ShouldNotContain(p => p.Name.Contains('\n'),
            "the fallback must skip multi-line metadata blobs");
        draft.Pins.ShouldNotContain(p => p.Name.Contains("cellname"));
        draft.Pins.Count.ShouldBe(2, "the waveguide stripe's two edge-heuristic pins remain");
        result.Infos.ShouldNotContain(i => i.Contains("non-standard layer"),
            "no label fallback ran — the blob is the only text and is not a label");
    }

    [Fact]
    public async Task BlackBox_MultiLineMetadataLabel_DoesNotBecomePin()
    {
        // Black-box mode promotes every nested label to a context-prefixed pin —
        // the same multi-line filter must apply before prefixing.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("dev", 0, 0)
            .EndCell()
            .BeginCell("dev")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "o1", 0, 2000)
                .Text(1, 10, "cellname: dev\nfoundry_pdk: test_pdk", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { Mode = GdsHierarchyImportMode.BlackBox });

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.Pins.Select(p => p.Name).ShouldContain("dev_o1");
        draft.Pins.ShouldNotContain(p => p.Name.Contains('\n'),
            "the multi-line metadata blob must not become a black-box pin");
    }

    // ── Warning quality ──────────────────────────────────────────────────────

    [Fact]
    public async Task Explode_ArrayWithNonCardinalRotation_WarnsOncePerReferenceWithMemberCount()
    {
        // A 3×3 AREF rotated 45° expands to 9 instances — the snap warning must
        // collapse into ONE per reference (with member count), not flood nine.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .ARef("wg", columns: 3, rows: 3, originX: 0, originY: 0,
                    columnSpacingDbUnits: 20000, rowSpacingDbUnits: 10000, angleDegrees: 45.0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Instances.Count.ShouldBe(9);
        var rotationWarnings = result.Warnings.Where(w => w.Contains("non-cardinal")).ToList();
        var warning = rotationWarnings.ShouldHaveSingleItem(
            "an AREF must not flood one identical warning per member");
        warning.ShouldContain("9 instances");
        warning.ShouldContain("wg#0");
        warning.ShouldContain("45");
    }

    [Fact]
    public async Task Explode_NegativeMagnification_WarnsAbout180DegreeSnapError()
    {
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0, magnification: -2.0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.Warnings.ShouldContain(w => w.Contains("NEGATIVE") && w.Contains("180"));
    }

    [Fact]
    public async Task Explode_KnownComponentSizeMismatch_WarnsPinsUnscaledAndConnectionsUncertain()
    {
        // PDK says 30×10 µm but the GDS cell measures 60×10 µm: pins are mapped
        // unscaled onto the GDS bbox, so reconstructed connections may be wrong —
        // the warning must say exactly that.
        var known = new KnownComponent(
            "mmi1x2", "testpdk", 30, 10,
            new[]
            {
                Pin("o1", 0, 5, 180),
                Pin("o2", 30, 5, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2", 0, 0)
            .EndCell()
            .BeginCell("mmi1x2")
                .Boundary(1, 0, (0, 0), (60000, 0), (60000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "mmi1x2" ? known : null,
            });

        var warning = result.Warnings.First(w => w.Contains("UNSCALED"));
        warning.ShouldContain("geometrically incorrect");
    }

    [Fact]
    public async Task Explode_PinlessDraft_ImporterStaysSilent_ServiceOwnsThatWarning()
    {
        // "blob" has no waveguide-layer geometry and no port labels → no pins.
        // The importer deliberately does NOT warn here: the service reports the
        // more actionable "geometry-only component" message when it registers
        // the draft — two warnings for the same fact would be noise.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("blob", 0, 0)
            .EndCell()
            .BeginCell("blob")
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        result.ImportedCellDrafts.ShouldHaveSingleItem().Pins.ShouldBeEmpty();
        result.Warnings.ShouldNotContain(w => w.Contains("no pins"));
    }

    [Fact]
    public async Task Explode_CellNameWithControlCharacters_RawCodeStaysValidPython()
    {
        // A GDS STRING may contain control characters (e.g. a newline) — the
        // emitted Python snippet must not break on them.
        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("weird\ncell", 0, 0)
            .EndCell()
            .BeginCell("weird\ncell")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        var draft = result.ImportedCellDrafts.ShouldHaveSingleItem();
        draft.RawCode.ShouldNotContain("weird\ncell");
        draft.RawCode.ShouldContain("weird_cell");
    }

    // ── Pin-anchored placement of known components ──────────────────────────

    [Fact]
    public async Task Explode_KnownComponentWithMarkerInflatedBBox_PlacesPinAnchoredWithoutWarning()
    {
        // The SiEPIC bond-pad shape (issue #811): the foundry cell's m_pin
        // marker paths inflate the cell bbox to 115.2×115.2 µm while the real
        // pad is the template's 100×100 and the restored 'elec' label sits at
        // the TRUE pin position (50, 50) in pad coordinates — (57.6, 57.6) in
        // the inflated bbox frame. The pins are authoritative: the placement
        // must follow the label, not the bbox (bbox-top-left mapping would
        // place the pad 7.6 µm off), and the pure bbox inflation must NOT fire
        // the size-mismatch warning.
        var known = new KnownComponent(
            "pad", "testpdk", 100, 100,
            new[] { Pin("elec", 50, 50, 0) });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("pad", 0, 0)
                .SRef("probe", 50000, 48000)
            .EndCell()
            .BeginCell("pad")
                // The real pad metal (11,0) at the true 100×100 box…
                .Boundary(11, 0, (0, 0), (100000, 0), (100000, 100000), (0, 100000), (0, 0))
                // …and the marker frame (non-waveguide layer) inflating the bbox to 115.2 µm.
                .Boundary(111, 0, (-7600, -7600), (107600, -7600), (107600, 107600), (-7600, 107600), (-7600, -7600))
                .Text(1, 10, "elec", 50000, 50000)
            .EndCell()
            .WaveguideCell("probe")
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "pad" ? known : null,
            });

        // Top bbox = the inflated pad bbox (−7.6,−7.6)–(107.6,107.6): the pad's
        // TRUE box lands at app (7.6, 7.6) — the bbox mapping would say (0, 0).
        var pad = result.Instances.Single(i => i.CellName == "pad");
        pad.PositionXUm.ShouldBe(7.6, Tolerance);
        pad.PositionYUm.ShouldBe(7.6, Tolerance);
        result.Warnings.ShouldBeEmpty(
            "marker-inflated bbox with matching pins is benign — no size warning");

        // The projected template pin sits at the label's TRUE position, so the
        // abutting probe pin reconstructs a connection (previously the pad's
        // pin was projected 7.6 µm off and missed).
        var connection = result.Connections.ShouldHaveSingleItem();
        connection.A.PinName.ShouldBe("elec");
        connection.B.PinName.ShouldBe("in");
        connection.XUm.ShouldBe(57.6, Tolerance);
        connection.YUm.ShouldBe(57.6, Tolerance);
    }

    [Fact]
    public async Task Explode_KnownComponentWithMismatchedPinLabels_WarnsButPlacesPinAnchored()
    {
        // A genuine pin-layout mismatch (the cell's pins are NOT a rigid
        // translation of the template's — here 40 µm apart vs the template's
        // 30): the pins still anchor the placement (best fit — the mean
        // translation), but the deviation earns ONE warning per cell.
        var known = new KnownComponent(
            "mmi", "testpdk", 30, 10,
            new[]
            {
                Pin("o1", 0, 5, 180),
                Pin("o2", 30, 5, 0),
            });

        var library = await ReadLibraryAsync(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi", 0, 0)
                .SRef("mmi", 100000, 0)
            .EndCell()
            .BeginCell("mmi")
                .Boundary(111, 0, (0, 0), (40000, 0), (40000, 10000), (0, 10000), (0, 0))
                .Text(1, 10, "o1", 0, 5000)
                .Text(1, 10, "o2", 40000, 5000)
            .EndCell()
            .EndLibrary()
            .ToArray());

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions
            {
                ResolveKnownComponent = name => name == "mmi" ? known : null,
            });

        // Per-pin deltas (0,0) and (10,0) → mean (5,0), deviation 5 µm each.
        var warning = result.Warnings.ShouldHaveSingleItem(
            "two instances of the same cell collapse into ONE pin-mismatch warning");
        warning.ShouldContain("'mmi'");
        warning.ShouldContain("pin layout");
        warning.ShouldContain("5");
        warning.ShouldContain("geometrically incorrect");

        // Best-fit placement: the template's 30×10 box shifted by the mean
        // delta (5, 0) — instance 2 adds its GDS offset (100, 0).
        result.Instances[0].PositionXUm.ShouldBe(5, Tolerance);
        result.Instances[0].PositionYUm.ShouldBe(0, Tolerance);
        result.Instances[1].PositionXUm.ShouldBe(105, Tolerance);
        result.Instances[1].PositionYUm.ShouldBe(0, Tolerance);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static DetectedPin Pin(string name, double x, double y, double angle) =>
        new() { Name = name, XUm = x, YUm = y, AngleDegrees = angle, Source = DetectedPinSource.Label };

    private static async Task<GdsLibrary> ReadLibraryAsync(byte[] gds) =>
        await new GdsReader().ReadAsync(new MemoryStream(gds));

    /// <summary>The PdkLoader rule: pins within [0, W] × [0, H] (±1 µm tolerance).</summary>
    /// <summary>
    /// Conservation invariant: every direct top-cell instance (SREF + expanded
    /// AREF members) is either imported as a placed instance or covered by an
    /// explicit skip note naming the cell and count — a GDS import must never
    /// lose components silently.
    /// </summary>
    [Fact]
    public async Task Explode_EveryDirectInstanceIsImportedOrExplicitlySkipped()
    {
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("WG", 0, 0)
                .SRef("WG", 0, 20000)
                .ARef("WG", columns: 2, rows: 2, originX: 40000, originY: 0,
                    columnSpacingDbUnits: 20000, rowSpacingDbUnits: 20000)
                .SRef("EMPTY", 100000, 0)
                .SRef("EMPTY", 120000, 0)
            .EndCell()
            .WaveguideCell("WG")
            .BeginCell("EMPTY").EndCell()
            .EndLibrary()
            .ToArray();
        var library = await new GdsReader().ReadAsync(new MemoryStream(gds));

        var circuit = await GdsHierarchyImporter.ImportAsync(library, "TOP", new GdsHierarchyImportOptions());

        int directInstances = library.Cells["TOP"].Elements
            .OfType<GdsReference>()
            .Sum(r => r.Rows * r.Columns);
        directInstances.ShouldBe(8, "fixture: 2 SREFs + 2×2 AREF of WG, 2 SREFs of EMPTY");

        circuit.Instances.Count.ShouldBe(6, "the six WG instances all import");
        var skipNote = circuit.Infos.Single(i => i.Contains("has no geometry", StringComparison.Ordinal));
        skipNote.ShouldContain("'EMPTY'");
        skipNote.ShouldContain("2 instance(s)");

        // The invariant itself: imported + explicitly skipped == everything the GDS placed.
        (circuit.Instances.Count + 2).ShouldBe(directInstances);
    }

    private static void AssertPinsWithinDraftBounds(GdsCellDraft draft)
    {
        foreach (var pin in draft.Pins)
        {
            pin.XUm.ShouldBeGreaterThanOrEqualTo(-1.0);
            pin.XUm.ShouldBeLessThanOrEqualTo(draft.WidthUm + 1.0);
            pin.YUm.ShouldBeGreaterThanOrEqualTo(-1.0);
            pin.YUm.ShouldBeLessThanOrEqualTo(draft.HeightUm + 1.0);
        }
    }
}

/// <summary>GDS fixture cell builders shared by the hierarchy importer tests.</summary>
file static class GdsHierarchyTestCells
{
    /// <summary>
    /// 10×4 µm cell, built like a real gdsfactory waveguide: a 0.5 µm core
    /// stripe (y ∈ [1.75, 2.25]) on the waveguide layer (1,0), an extent
    /// rectangle on the non-waveguide layer (111,0) — it sizes the bbox
    /// without firing the edge heuristic — and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name, int inY = 2000, int outY = 2000) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, inY)
                .Text(1, 10, "out", 10000, outY)
            .EndCell();
}

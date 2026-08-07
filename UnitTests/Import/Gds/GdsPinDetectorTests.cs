using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsPinDetector"/>. Fixtures are hand-built
/// <see cref="FlattenedGdsCell"/> instances (GDS space: µm, Y-up) plus one
/// end-to-end test through <see cref="GdsReader"/>/<see cref="GdsCellFlattener"/>.
///
/// The detector emits app-space values: Y-down, origin at the bbox top-left,
/// angles 0° = east, 90° = down (bottom edge), 180° = west, 270° = up (top
/// edge). The visual top edge is the GDS MaxY line, the visual bottom edge is
/// GDS MinY.
/// </summary>
public class GdsPinDetectorTests
{
    private const double Tolerance = 1e-9;

    private static readonly GdsBoundingBox Box10x4 = new(0, 0, 10, 4);

    // ── Label pins ───────────────────────────────────────────────────────────

    [Fact]
    public void Label_OnPortLayerAtLeftEdge_ProducesWestOutwardPin()
    {
        var cell = Cell(Label(1, 10, "o1", x: 0, y: 3));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.Name.ShouldBe("o1");
        pin.Source.ShouldBe(DetectedPinSource.Label);
        pin.XUm.ShouldBe(0, Tolerance);
        pin.YUm.ShouldBe(1, Tolerance); // 4 − 3: Y flipped, origin at top
        pin.AngleDegrees.ShouldBe(180, Tolerance); // left edge → west
        pin.WidthUm.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void Label_SlightlyInsideLeftEdge_StillUsesThatEdge()
    {
        // Anchor within EdgeTouchToleranceUm of the edge: angle snaps to the
        // edge's outward normal, the position stays at the anchor.
        var cell = Cell(Label(1, 10, "o1", x: 0.0005, y: 2));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.AngleDegrees.ShouldBe(180, Tolerance);
        pin.XUm.ShouldBe(0.0005, Tolerance);
        pin.YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public void Label_NotTouchingAnyEdge_UsesNearestEdge()
    {
        // Interior anchor, closest to the bottom edge (GDS MinY = visual bottom).
        var cell = Cell(Label(1, 10, "o1", x: 5, y: 0.5));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.AngleDegrees.ShouldBe(90, Tolerance); // bottom edge → down in app convention
        pin.XUm.ShouldBe(5, Tolerance);
        pin.YUm.ShouldBe(3.5, Tolerance); // 4 − 0.5
    }

    [Fact]
    public void Label_OnTopAndBottomEdges_TopIs270BottomIs90()
    {
        // GDS MaxY is the visual top edge (appY = 0): outward = up = 270°.
        // GDS MinY is the visual bottom edge: outward = down = 90°.
        var cell = Cell(
            Label(1, 10, "top", x: 5, y: 4),
            Label(1, 10, "bottom", x: 5, y: 0));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.Count.ShouldBe(2);
        pins[0].Name.ShouldBe("top"); // top edge sorts before bottom edge
        pins[0].AngleDegrees.ShouldBe(270, Tolerance);
        pins[0].YUm.ShouldBe(0, Tolerance);
        pins[1].Name.ShouldBe("bottom");
        pins[1].AngleDegrees.ShouldBe(90, Tolerance);
        pins[1].YUm.ShouldBe(4, Tolerance);
    }

    [Fact]
    public void Label_OnNonPortLayer_IsIgnored()
    {
        var cell = Cell(
            Label(2, 10, "wrong-layer", x: 0, y: 2),
            Label(1, 11, "wrong-texttype", x: 0, y: 3));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldBeEmpty();
    }

    // ── Edge heuristic ───────────────────────────────────────────────────────

    [Fact]
    public void Waveguide_TouchingRightEdge_ProducesEastPinWithSegmentWidth()
    {
        // 1 µm tall waveguide end face on the right edge, GDS y ∈ [1, 2].
        var cell = Cell(Poly(1, 0, (8, 1), (10, 1), (10, 2), (8, 2), (8, 1)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.Source.ShouldBe(DetectedPinSource.EdgeHeuristic);
        pin.Name.ShouldBe("heur_1");
        pin.AngleDegrees.ShouldBe(0, Tolerance); // right edge → east
        pin.XUm.ShouldBe(10, Tolerance);
        pin.YUm.ShouldBe(2.5, Tolerance); // 4 − 1.5 (segment midpoint, Y flipped)
        pin.WidthUm.ShouldBe(1, Tolerance);
    }

    [Fact]
    public void Waveguide_TouchingTopAndBottomEdges_TopIs270BottomIs90()
    {
        var cell = Cell(
            Poly(1, 0, (4, 4), (6, 4), (6, 3), (4, 3), (4, 4)), // 2 µm face on GDS MaxY (visual top)
            Poly(1, 0, (4, 0), (6, 0), (6, 1), (4, 1), (4, 0))); // 2 µm face on GDS MinY (visual bottom)

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.Count.ShouldBe(2);
        pins[0].Name.ShouldBe("heur_1"); // top edge sorts before bottom edge
        pins[0].AngleDegrees.ShouldBe(270, Tolerance);
        pins[0].XUm.ShouldBe(5, Tolerance);
        pins[0].YUm.ShouldBe(0, Tolerance);
        pins[0].WidthUm.ShouldBe(2, Tolerance);
        pins[1].Name.ShouldBe("heur_2");
        pins[1].AngleDegrees.ShouldBe(90, Tolerance);
        pins[1].XUm.ShouldBe(5, Tolerance);
        pins[1].YUm.ShouldBe(4, Tolerance);
        pins[1].WidthUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public void Polygon_OnNonWaveguideLayer_IsIgnored()
    {
        var cell = Cell(
            Poly(2, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)),
            Poly(1, 5, (0, 3), (3, 3), (3, 3.5), (0, 3.5), (0, 3)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldBeEmpty();
    }

    [Fact]
    public void ClosingPointDuplication_CreatesNoPhantomPins()
    {
        var cell = Cell(
            // Standard closed rect (first point repeated): one left-edge face, width 1.
            Poly(1, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)),
            // Extra consecutive duplicate vertex ON the edge: zero-length touch must vanish.
            Poly(1, 0, (0, 3), (0, 3), (3, 3), (3, 3.5), (0, 3.5), (0, 3)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.Count.ShouldBe(2);
        pins[0].Name.ShouldBe("heur_1"); // smaller appY sorts first
        pins[0].YUm.ShouldBe(0.75, Tolerance); // 4 − 3.25
        pins[0].WidthUm.ShouldBe(0.5, Tolerance);
        pins[1].Name.ShouldBe("heur_2");
        pins[1].YUm.ShouldBe(2.5, Tolerance); // 4 − 1.5
        pins[1].WidthUm.ShouldBe(1, Tolerance);
    }

    [Fact]
    public void Touches_AdjacentWithinTolerance_MergeIntoOnePin()
    {
        // Two waveguide faces on the left edge, 0.0005 µm apart (< tolerance).
        var cell = Cell(
            Poly(1, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)),
            Poly(1, 0, (0, 2.0005), (3, 2.0005), (3, 3), (0, 3), (0, 2.0005)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.WidthUm.ShouldBe(2, Tolerance); // merged interval [1, 3]
        pin.YUm.ShouldBe(2, Tolerance); // 4 − 2
        pin.XUm.ShouldBe(0, Tolerance);
        pin.AngleDegrees.ShouldBe(180, Tolerance);
    }

    [Fact]
    public void Touches_SeparatedBeyondTolerance_StaySeparate()
    {
        // Same faces, but 0.002 µm apart (> tolerance).
        var cell = Cell(
            Poly(1, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)),
            Poly(1, 0, (0, 2.002), (3, 2.002), (3, 3), (0, 3), (0, 2.002)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.Count.ShouldBe(2);
        pins[0].WidthUm.ShouldBe(0.998, Tolerance);
        pins[1].WidthUm.ShouldBe(1, Tolerance);
    }

    [Fact]
    public void Touch_WiderThanMaxPinWidth_IsFiltered()
    {
        var box = new GdsBoundingBox(0, 0, 200, 200);
        var cell = Cell(Poly(1, 0, (0, 10), (10, 10), (10, 160), (0, 160), (0, 10))); // 150 µm face

        var pins = GdsPinDetector.Detect(cell, box);

        pins.ShouldBeEmpty();
    }

    [Fact]
    public void Touch_NarrowerThanMinPinWidth_IsFiltered()
    {
        var box = new GdsBoundingBox(0, 0, 200, 200);
        var cell = Cell(Poly(1, 0, (0, 10), (10, 10), (10, 10.05), (0, 10.05), (0, 10))); // 0.05 µm face

        var pins = GdsPinDetector.Detect(cell, box);

        pins.ShouldBeEmpty();
    }

    // ── Label/heuristic interaction ──────────────────────────────────────────

    [Fact]
    public void LabelAndTouch_AtSameSpot_YieldsOnlyLabelPin()
    {
        var cell = Cell(
            Label(1, 10, "o1", x: 0, y: 2),
            Poly(1, 0, (0, 1.75), (3, 1.75), (3, 2.25), (0, 2.25), (0, 1.75)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.Source.ShouldBe(DetectedPinSource.Label);
        pin.Name.ShouldBe("o1");
        pin.WidthUm.ShouldBe(0, Tolerance);
        pin.AngleDegrees.ShouldBe(180, Tolerance);
    }

    // ── Label direction from local geometry ──────────────────────────────────

    [Fact]
    public void Label_OnWaveguideEndFaceInsideLargeBBox_UsesSegmentNormal_AllOrientations()
    {
        // A 100 × 80 µm black-box-sized cell whose four waveguide stubs end deep
        // inside the bbox, each label sitting ON its stub's end face. The bbox
        // edge rule would point every pin at the nearest outer edge; the local
        // geometry must win and point along the waveguide axis instead.
        var box = new GdsBoundingBox(0, 0, 100, 80);
        var cell = Cell(
            // East-pointing stub: end face x = 14, body extending to -X.
            Poly(1, 0, (10, 39.75), (14, 39.75), (14, 40.25), (10, 40.25), (10, 39.75)),
            Label(1, 10, "east", x: 14, y: 40),
            // West-pointing stub: end face x = 86, body extending to +X.
            Poly(1, 0, (86, 39.75), (90, 39.75), (90, 40.25), (86, 40.25), (86, 39.75)),
            Label(1, 10, "west", x: 86, y: 40),
            // Down-pointing stub (app convention): body extending to GDS +Y.
            Poly(1, 0, (49.75, 60), (50.25, 60), (50.25, 64), (49.75, 64), (49.75, 60)),
            Label(1, 10, "down", x: 50, y: 60),
            // Up-pointing stub (app convention): body extending to GDS -Y.
            Poly(1, 0, (49.75, 16), (50.25, 16), (50.25, 20), (49.75, 20), (49.75, 16)),
            Label(1, 10, "up", x: 50, y: 20));

        var pins = GdsPinDetector.Detect(cell, box);

        pins.Count.ShouldBe(4);
        // Sorted by nearest bbox edge (left, top, right, bottom): east → left,
        // down → top, west → right, up → bottom.
        pins[0].Name.ShouldBe("east");
        pins[0].AngleDegrees.ShouldBe(0, Tolerance);
        pins[1].Name.ShouldBe("down");
        pins[1].AngleDegrees.ShouldBe(90, Tolerance);
        pins[2].Name.ShouldBe("west");
        pins[2].AngleDegrees.ShouldBe(180, Tolerance);
        pins[3].Name.ShouldBe("up");
        pins[3].AngleDegrees.ShouldBe(270, Tolerance);
    }

    [Fact]
    public void Label_OnDiagonalSegment_FlipsYIntoAppConvention()
    {
        // 45° face with GDS-space outward normal (+1, +1)/√2 (NE). Without the
        // Y-flip this would come out as 45°; the app convention (Y-down) must
        // mirror it to 315°.
        var cell = Cell(
            Poly(1, 0, (4, 4), (6, 4), (4, 6), (4, 4)),
            Label(1, 10, "diag", x: 5.05, y: 5.05));

        var pins = GdsPinDetector.Detect(cell, new GdsBoundingBox(0, 0, 10, 10));

        var pin = pins.ShouldHaveSingleItem();
        pin.Source.ShouldBe(DetectedPinSource.Label);
        pin.AngleDegrees.ShouldBe(315, Tolerance);
    }

    [Fact]
    public void Label_FarFromGeometry_FallsBackToBoundingBoxEdge()
    {
        // Waveguide stub ends at x = 3, the label floats 2 µm past its end face
        // — beyond LabelGeometryTouchToleranceUm (1.0), so the bbox rule applies.
        var cell = Cell(
            Poly(1, 0, (1, 1.75), (3, 1.75), (3, 2.25), (1, 2.25), (1, 1.75)),
            Label(1, 10, "o1", x: 5, y: 3));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        var pin = pins.ShouldHaveSingleItem();
        pin.AngleDegrees.ShouldBe(270, Tolerance); // nearest bbox edge: top (GDS MaxY)
        pin.IsElectrical.ShouldBeNull("no polygon near, and 'o1' is no electrical name");
    }

    [Fact]
    public void Label_OnMetalPolygonEndFaceInsideLargeBBox_UsesMetalSegmentNormal()
    {
        // Metal pads count as direction geometry too: the label sits on the pad's
        // right end face, deep inside the bbox.
        var box = new GdsBoundingBox(0, 0, 100, 80);
        var cell = Cell(
            Poly(11, 0, (10, 39.75), (14, 39.75), (14, 40.25), (10, 40.25), (10, 39.75)),
            Label(1, 10, "anode", x: 14, y: 40));

        var pins = GdsPinDetector.Detect(cell, box);

        var pin = pins.ShouldHaveSingleItem();
        pin.AngleDegrees.ShouldBe(0, Tolerance);
    }

    // ── Label pin kind inference ─────────────────────────────────────────────

    [Fact]
    public void Label_OnMetalPolygonOutline_InfersElectrical()
    {
        var cell = Cell(
            Poly(11, 0, (4, 1.75), (6, 1.75), (6, 2.25), (4, 2.25), (4, 1.75)),
            Label(1, 10, "p1", x: 6, y: 2)); // ON the pad's right edge

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBe(true);
    }

    [Fact]
    public void Label_InsideMetalPad_InfersElectrical()
    {
        // Pad label at the pad CENTER, farther from the outline than the touch
        // tolerance: the interior still proves the metal contact (the user's
        // black-box case).
        var cell = Cell(
            Poly(12, 0, (2, 0.5), (8, 0.5), (8, 3.5), (2, 3.5), (2, 0.5)),
            Label(1, 10, "p1", x: 5, y: 2));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBe(true);
    }

    [Fact]
    public void Label_OnWaveguidePolygon_KeepsUnknownKind()
    {
        // Waveguide evidence means optical — expressed as kind-UNKNOWN (null):
        // a later metal-route match may still overrule it with stronger evidence.
        var cell = Cell(
            Poly(1, 0, (0, 1.75), (3, 1.75), (3, 2.25), (0, 2.25), (0, 1.75)),
            Label(1, 10, "o1", x: 0, y: 2));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBeNull();
    }

    [Fact]
    public void Label_TouchingMetalAndWaveguide_MetalWins()
    {
        // Electrode over waveguide (eopm-style): both layer classes touch the
        // anchor; metal is the stronger evidence.
        var cell = Cell(
            Poly(1, 0, (1, 1.75), (4, 1.75), (4, 2.25), (1, 2.25), (1, 1.75)),
            Poly(11, 0, (1, 1.9), (4, 1.9), (4, 2.1), (1, 2.1), (1, 1.9)),
            Label(1, 10, "p1", x: 2, y: 2));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBe(true);
    }

    [Theory]
    [InlineData("anode")]
    [InlineData("CATHODE")]
    [InlineData("elec1")]
    [InlineData("Bond_Pad_34")]
    [InlineData("gnd")]
    [InlineData("VCC")]
    [InlineData("vdd_0")]
    public void Label_WithElectricalNameAndNoGeometry_InfersElectrical(string label)
    {
        var pins = GdsPinDetector.Detect(Cell(Label(1, 10, label, x: 5, y: 2)), Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBe(true);
    }

    [Theory]
    [InlineData("o1")]
    [InlineData("in")]
    [InlineData("port0")]
    public void Label_WithOpticalNameAndNoGeometry_KeepsUnknownKind(string label)
    {
        var pins = GdsPinDetector.Detect(Cell(Label(1, 10, label, x: 5, y: 2)), Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBeNull();
    }

    [Fact]
    public void Label_WithElectricalNameOnWaveguide_GeometryBeatsName()
    {
        // The name heuristic is only the fallback: waveguide evidence wins.
        var cell = Cell(
            Poly(1, 0, (0, 1.75), (3, 1.75), (3, 2.25), (0, 2.25), (0, 1.75)),
            Label(1, 10, "anode", x: 0, y: 2));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBeNull();
    }

    [Fact]
    public void HeuristicPin_OnWaveguideEdge_KeepsUnknownKind()
    {
        var cell = Cell(Poly(1, 0, (8, 1), (10, 1), (10, 2), (8, 2), (8, 1)));

        var pins = GdsPinDetector.Detect(cell, Box10x4);

        pins.ShouldHaveSingleItem().IsElectrical.ShouldBeNull();
    }

    // ── Options ──────────────────────────────────────────────────────────────

    [Fact]
    public void CustomLayers_AreRespected()
    {
        var options = new GdsPinDetectionOptions
        {
            PortLayers = [(3, 0)],
            WaveguideLayers = [(7, 1)],
        };
        var cell = Cell(
            Label(3, 0, "p1", x: 0, y: 2),
            Label(1, 10, "ignored", x: 0, y: 3),
            Poly(7, 1, (8, 1), (10, 1), (10, 2), (8, 2), (8, 1)),
            Poly(1, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)));

        var pins = GdsPinDetector.Detect(cell, Box10x4, options);

        pins.Count.ShouldBe(2);
        pins[0].Name.ShouldBe("p1");
        pins[0].Source.ShouldBe(DetectedPinSource.Label);
        pins[1].Name.ShouldBe("heur_1");
        pins[1].AngleDegrees.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void CustomWidthBounds_AreRespected()
    {
        var box = new GdsBoundingBox(0, 0, 200, 200);
        var wideCell = Cell(Poly(1, 0, (0, 10), (10, 10), (10, 160), (0, 160), (0, 10)));
        var narrowCell = Cell(Poly(1, 0, (0, 10), (10, 10), (10, 10.05), (0, 10.05), (0, 10)));

        var widePins = GdsPinDetector.Detect(wideCell, box,
            new GdsPinDetectionOptions { MaxPinWidthUm = 200 });
        var narrowPins = GdsPinDetector.Detect(narrowCell, box,
            new GdsPinDetectionOptions { MinPinWidthUm = 0.01 });

        widePins.ShouldHaveSingleItem().WidthUm.ShouldBe(150, Tolerance);
        narrowPins.ShouldHaveSingleItem().WidthUm.ShouldBe(0.05, Tolerance);
    }

    // ── Empty / degenerate input ─────────────────────────────────────────────

    [Fact]
    public void EmptyCell_ReturnsEmptyList()
    {
        var pins = GdsPinDetector.Detect(Cell(), Box10x4);

        pins.ShouldBeEmpty();
    }

    [Fact]
    public void DegenerateBoundingBox_ReturnsEmptyList()
    {
        var cell = Cell(
            Label(1, 10, "o1", x: 0, y: 2),
            Poly(1, 0, (0, 1), (3, 1), (3, 2), (0, 2), (0, 1)));

        GdsPinDetector.Detect(cell, GdsBoundingBox.Empty).ShouldBeEmpty();
        GdsPinDetector.Detect(cell, new GdsBoundingBox(0, 0, 0, 4)).ShouldBeEmpty();
        GdsPinDetector.Detect(cell, new GdsBoundingBox(0, 0, 10, 0)).ShouldBeEmpty();
    }

    // ── Ordering and naming ──────────────────────────────────────────────────

    [Fact]
    public void Pins_AreSortedByEdgeThenPosition_HeuristicNamesAssignedAfterSorting()
    {
        var box = new GdsBoundingBox(0, 0, 10, 10);
        var cell = Cell(
            Poly(1, 0, (0, 2), (2, 2), (2, 2.5), (0, 2.5), (0, 2)),       // left, appY 7.75
            Poly(1, 0, (0, 8), (2, 8), (2, 8.5), (0, 8.5), (0, 8)),       // left, appY 1.75
            Label(1, 10, "o1", x: 0, y: 5),                               // left, appY 5
            Poly(1, 0, (4, 10), (5, 10), (5, 9), (4, 9), (4, 10)),        // top
            Poly(1, 0, (10, 4), (10, 5), (9, 5), (9, 4), (10, 4)),        // right
            Poly(1, 0, (3, 0), (4, 0), (4, 1), (3, 1), (3, 0)));          // bottom

        var pins = GdsPinDetector.Detect(cell, box);

        pins.Count.ShouldBe(6);
        // Edge order: left, top, right, bottom; within an edge by app-space position.
        pins[0].Name.ShouldBe("heur_1");
        pins[0].YUm.ShouldBe(1.75, Tolerance);
        pins[1].Name.ShouldBe("o1"); // label keeps its name and consumes no heur_ number
        pins[1].YUm.ShouldBe(5, Tolerance);
        pins[2].Name.ShouldBe("heur_2");
        pins[2].YUm.ShouldBe(7.75, Tolerance);
        pins[3].Name.ShouldBe("heur_3");
        pins[3].AngleDegrees.ShouldBe(270, Tolerance);
        pins[4].Name.ShouldBe("heur_4");
        pins[4].AngleDegrees.ShouldBe(0, Tolerance);
        pins[5].Name.ShouldBe("heur_5");
        pins[5].AngleDegrees.ShouldBe(90, Tolerance);
        pins[5].YUm.ShouldBe(10, Tolerance);
    }

    // ── End to end through the real reader ───────────────────────────────────

    [Fact]
    public async Task EndToEnd_GdsFactoryStyleWaveguide_LabelAndHeuristicPin()
    {
        // 10 × 0.5 µm waveguide ending at the left/right bbox edges, a DevRec-style
        // marker on a non-waveguide layer sizing the cell to 10 × 4 µm, and a port
        // label on (1, 10) covering the left end face. 1000 db units = 1 µm.
        var gds = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("WG")
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(2, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "o1", 0, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray();

        using var stream = new MemoryStream(gds);
        var library = await new GdsReader().ReadAsync(stream);
        var flattener = new GdsCellFlattener(library);
        var flattened = flattener.Flatten("WG");
        var bbox = flattener.GetBoundingBox("WG");

        var pins = GdsPinDetector.Detect(flattened, bbox);

        pins.Count.ShouldBe(2);
        // Left edge: label pin only — the waveguide face below it is covered.
        pins[0].Name.ShouldBe("o1");
        pins[0].Source.ShouldBe(DetectedPinSource.Label);
        pins[0].XUm.ShouldBe(0, Tolerance);
        pins[0].YUm.ShouldBe(2, Tolerance); // 4 − 2
        pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        // Right edge: heuristic pin from the waveguide end face.
        pins[1].Name.ShouldBe("heur_1");
        pins[1].Source.ShouldBe(DetectedPinSource.EdgeHeuristic);
        pins[1].XUm.ShouldBe(10, Tolerance);
        pins[1].YUm.ShouldBe(2, Tolerance);
        pins[1].AngleDegrees.ShouldBe(0, Tolerance);
        pins[1].WidthUm.ShouldBe(0.5, Tolerance);
    }

    // ── Spatial-grid equivalence ─────────────────────────────────────────────

    [Fact]
    public void Detect_SeededLabelField_IdenticalToBruteForceReference()
    {
        // ~110 labels over ~700 waveguide/metal segments in engineered spots
        // (exact touches, ordinal ties between coincident faces, the inclusive
        // tolerance boundary, near-misses, interior-only containment, layer
        // mixing, degenerate vertices) plus dense unstructured clusters. The
        // spatial grid is a candidate pre-filter only, so the detected pins —
        // names, positions, angles, widths, kinds, and their ORDER — must be
        // identical to the sequential reference scan.
        var box = new GdsBoundingBox(0, 0, 300, 240);
        var random = new Random(20260806);
        var elements = new List<GdsElement>();
        int labelCount = 0;

        void AddLabel(string name, double x, double y)
        {
            elements.Add(Label(1, 10, name, x, y));
            labelCount++;
        }

        GdsPolygon Rect(int layer, int dataType, double x0, double y0, double x1, double y1) =>
            Poly(layer, dataType, (x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0));

        // Spot pitch 20 µm ≫ the 1 µm touch tolerance: scenarios never interact.
        for (int n = 0; n < 100; n++)
        {
            double sx = 15 + (n % 10 * 20.0);
            double sy = 15 + (n / 10 * 20.0);
            switch (n % 10)
            {
                case 0: // label exactly on a stub end face, random orientation
                    elements.Add(random.Next(4) switch
                    {
                        0 => Rect(1, 0, sx - 4, sy - 0.25, sx, sy + 0.25),
                        1 => Rect(1, 0, sx, sy - 0.25, sx + 4, sy + 0.25),
                        2 => Rect(1, 0, sx - 0.25, sy - 4, sx + 0.25, sy),
                        _ => Rect(1, 0, sx - 0.25, sy, sx + 0.25, sy + 4),
                    });
                    AddLabel($"s{n}", sx, sy);
                    break;
                case 1: // two coincident faces, opposite normals — ordinal tie, first polygon wins
                    elements.Add(Rect(1, 0, sx - 3, sy - 0.25, sx, sy + 0.25));
                    elements.Add(Rect(1, 0, sx, sy - 0.25, sx + 3, sy + 0.25));
                    AddLabel($"tie{n}", sx, sy);
                    break;
                case 2: // anchor at EXACTLY the tolerance distance — inclusive boundary
                    elements.Add(Rect(1, 0, sx - 4, sy - 1, sx - 1, sy + 1));
                    AddLabel($"b{n}", sx, sy);
                    break;
                case 3: // just past the tolerance — bbox-edge fallback, name decides the kind
                    elements.Add(Rect(1, 0, sx - 4, sy - 0.5, sx - 1.01 - (0.3 * random.NextDouble()), sy + 0.5));
                    AddLabel(random.Next(2) == 0 ? $"anode_{n}" : $"o{n}", sx, sy);
                    break;
                case 4: // interior-only metal contact: pad center, outline far beyond tolerance
                    elements.Add(Rect(11, 0, sx - 5, sy - 5, sx + 5, sy + 5));
                    AddLabel($"p{n}", sx + random.NextDouble() - 0.5, sy + random.NextDouble() - 0.5);
                    break;
                case 5: // interior of a waveguide slab with an electrical NAME — geometry beats the name
                    elements.Add(Rect(1, 0, sx - 5, sy - 5, sx + 5, sy + 5));
                    AddLabel($"anode{n}", sx + random.NextDouble() - 0.5, sy + random.NextDouble() - 0.5);
                    break;
                case 6: // metal over waveguide, coincident faces: earliest polygon steers, metal kind wins
                    elements.Add(Rect(1, 0, sx - 3, sy - 0.25, sx, sy + 0.25));
                    elements.Add(Rect(11, 0, sx - 3, sy - 0.1, sx, sy + 0.1));
                    AddLabel($"m{n}", sx, sy);
                    break;
                case 7: // diagonal hypotenuse face — non-cardinal segment normal
                    elements.Add(Poly(1, 0, (sx - 2, sy - 2), (sx + 2, sy - 2), (sx - 2, sy + 2), (sx - 2, sy - 2)));
                    AddLabel($"d{n}", sx + 0.05, sy + 0.05);
                    break;
                case 8: // strictly closer LATER polygon must beat the earlier one — distance before ordinal
                    elements.Add(Rect(1, 0, sx - 4, sy - 0.5, sx - 0.5, sy + 0.5));
                    elements.Add(Rect(1, 0, sx + 0.3, sy - 0.5, sx + 4, sy + 0.5));
                    AddLabel($"near{n}", sx, sy);
                    break;
                default: // degenerate junk: duplicated vertex, unclosed outline, single point, ignored layer
                    elements.Add(Poly(1, 0,
                        (sx, sy - 0.5), (sx, sy - 0.5), (sx, sy + 0.5), (sx - 2, sy + 0.5), (sx - 2, sy - 0.5)));
                    elements.Add(Poly(1, 0, (sx + 3, sy)));
                    elements.Add(Rect(2, 0, sx - 1, sy - 1, sx + 1, sy + 1));
                    AddLabel($"g{n}", sx + 0.2, sy);
                    break;
            }
        }

        // Dense unstructured clusters (random rectangles of every layer class,
        // labels at random offsets around the tolerance window): no per-cluster
        // guarantees — the sequential-reference equality is the arbiter.
        string[] namePool = ["anode", "o1", "elec", "in", "pad", "wg"];
        for (int c = 0; c < 3; c++)
        {
            double cx = 250, cy = 40 + (c * 80.0);
            for (int r = 0; r < 10; r++)
            {
                (int layer, int dataType) = random.Next(3) switch { 0 => (1, 0), 1 => (11, 0), _ => (2, 0) };
                double x0 = cx + ((random.NextDouble() - 0.5) * 8);
                double y0 = cy + ((random.NextDouble() - 0.5) * 8);
                elements.Add(Rect(layer, dataType,
                    x0, y0, x0 + 0.5 + (2.5 * random.NextDouble()), y0 + 0.5 + (2.5 * random.NextDouble())));
            }
            for (int l = 0; l < 6; l++)
            {
                AddLabel($"{namePool[l]}_{c}{l}",
                    cx + ((random.NextDouble() - 0.5) * 10), cy + ((random.NextDouble() - 0.5) * 10));
            }
        }

        // Bounding-box edge touches: one heuristic pin survives, one is covered
        // by a label (which itself takes the face normal).
        elements.Add(Rect(1, 0, 0, 100, 3, 101));
        elements.Add(Rect(1, 0, 297, 50, 300, 51));
        AddLabel("cov", 0, 100.5);

        var cell = Cell(elements.ToArray());
        var expected = SequentialReferenceDetector.Detect(cell, box);
        var actual = GdsPinDetector.Detect(cell, box);

        expected.Count(p => p.Source == DetectedPinSource.Label).ShouldBe(labelCount,
            "every port-layer label must yield a pin");
        expected.ShouldContain(p => p.IsElectrical == true);
        expected.ShouldContain(p => p.IsElectrical == null);
        expected.ShouldContain(p => p.Source == DetectedPinSource.EdgeHeuristic);
        actual.ShouldBe(expected, "same pins, same order, same values as the sequential reference scan");
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static FlattenedGdsCell Cell(params GdsElement[] elements)
    {
        var cell = new FlattenedGdsCell { CellName = "TEST" };
        foreach (var element in elements)
        {
            switch (element)
            {
                case GdsPolygon polygon:
                    cell.Polygons.Add(polygon);
                    break;
                case GdsText text:
                    cell.Texts.Add(text);
                    break;
            }
        }
        return cell;
    }

    private static GdsPolygon Poly(int layer, int dataType, params (double X, double Y)[] points) =>
        new()
        {
            Layer = layer,
            DataType = dataType,
            Points = points.Select(p => new GdsPoint(p.X, p.Y)).ToList(),
        };

    private static GdsText Label(int layer, int textType, string text, double x, double y) =>
        new()
        {
            Layer = layer,
            TextType = textType,
            Text = text,
            Position = new GdsPoint(x, y),
        };

    /// <summary>
    /// The pre-grid sequential detector, kept verbatim as the semantic
    /// reference: the production detector's spatial pruning must reproduce its
    /// pins — values and ordering — exactly.
    /// </summary>
    private static class SequentialReferenceDetector
    {
        private enum CellEdge
        {
            Left = 0,
            Top = 1,
            Right = 2,
            Bottom = 3,
        }

        private readonly record struct Candidate(CellEdge Edge, DetectedPin Pin);

        private sealed record AnchorGeometry(
            double SegmentDistanceSquared,
            GdsPolygon Polygon,
            GdsPoint P1,
            GdsPoint P2,
            bool TouchesWaveguide,
            bool TouchesMetal);

        private const double InteriorProbeOffsetUm = 0.001;

        private static readonly string[] ElectricalLabelMarkers =
            ["anode", "cathode", "elec", "pad", "gnd", "vcc", "vdd"];

        public static IReadOnlyList<DetectedPin> Detect(
            FlattenedGdsCell flattened,
            GdsBoundingBox cellBBox,
            GdsPinDetectionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(flattened);
            options ??= new GdsPinDetectionOptions();
            options.Validate();

            var result = new List<DetectedPin>();
            if (cellBBox.Width <= 0 || cellBBox.Height <= 0)
                return result;

            double tolerance = options.EdgeTouchToleranceUm;

            var labelAnchors = new List<GdsPoint>();
            var candidates = new List<Candidate>();
            double geometryToleranceSquared =
                options.LabelGeometryTouchToleranceUm * options.LabelGeometryTouchToleranceUm;
            foreach (var text in flattened.Texts)
            {
                if (!ContainsLayer(options.PortLayers, text.Layer, text.TextType))
                    continue;

                CellEdge edge = NearestEdge(text.Position, cellBBox);
                var geometry = ProbeAnchorGeometry(text.Position, flattened.Polygons, options);
                labelAnchors.Add(text.Position);
                candidates.Add(new Candidate(edge, new DetectedPin
                {
                    Name = text.Text,
                    XUm = ToAppX(text.Position.X, cellBBox),
                    YUm = ToAppY(text.Position.Y, cellBBox),
                    AngleDegrees = geometry is not null && geometry.SegmentDistanceSquared <= geometryToleranceSquared
                        ? SegmentOutwardAngleDegrees(geometry.Polygon, geometry.P1, geometry.P2)
                        : OutwardAngleDegrees(edge),
                    WidthUm = 0,
                    Source = DetectedPinSource.Label,
                    IsElectrical = InferLabelPinKind(text.Text, geometry),
                }));
            }

            var touches = new SortedList<CellEdge, List<(double Start, double End)>>();
            foreach (var polygon in flattened.Polygons)
            {
                if (!ContainsLayer(options.WaveguideLayers, polygon.Layer, polygon.DataType))
                    continue;

                foreach (var (p1, p2) in Segments(polygon))
                {
                    CellEdge? edge = TouchingEdge(p1, p2, cellBBox, tolerance);
                    if (edge is null)
                        continue;

                    (double start, double end) = edge is CellEdge.Left or CellEdge.Right
                        ? (Math.Min(p1.Y, p2.Y), Math.Max(p1.Y, p2.Y))
                        : (Math.Min(p1.X, p2.X), Math.Max(p1.X, p2.X));
                    if (!touches.TryGetValue(edge.Value, out var list))
                        touches.Add(edge.Value, list = new List<(double, double)>());
                    list.Add((start, end));
                }
            }

            foreach (var (edge, intervals) in touches)
            {
                foreach (var (start, end) in MergeIntervals(intervals, tolerance))
                {
                    double width = end - start;
                    if (width < options.MinPinWidthUm || width > options.MaxPinWidthUm)
                        continue;

                    GdsPoint midpoint = MidpointOnEdge(edge, (start + end) / 2.0, cellBBox);
                    if (IsCoveredByLabel(midpoint, labelAnchors, tolerance))
                        continue;

                    candidates.Add(new Candidate(edge, new DetectedPin
                    {
                        Name = string.Empty,
                        XUm = ToAppX(midpoint.X, cellBBox),
                        YUm = ToAppY(midpoint.Y, cellBBox),
                        AngleDegrees = OutwardAngleDegrees(edge),
                        WidthUm = width,
                        Source = DetectedPinSource.EdgeHeuristic,
                    }));
                }
            }

            int heuristicCount = 0;
            foreach (var candidate in candidates
                .OrderBy(c => (int)c.Edge)
                .ThenBy(c => c.Edge is CellEdge.Left or CellEdge.Right ? c.Pin.YUm : c.Pin.XUm))
            {
                var pin = candidate.Pin;
                if (pin.Source == DetectedPinSource.EdgeHeuristic)
                    pin = pin with { Name = $"heur_{++heuristicCount}" };
                result.Add(pin);
            }

            return result;
        }

        private static double ToAppX(double gdsX, GdsBoundingBox bbox) => gdsX - bbox.MinX;

        private static double ToAppY(double gdsY, GdsBoundingBox bbox) => bbox.MaxY - gdsY;

        private static double OutwardAngleDegrees(CellEdge edge) => edge switch
        {
            CellEdge.Left => 180.0,
            CellEdge.Top => 270.0,
            CellEdge.Right => 0.0,
            CellEdge.Bottom => 90.0,
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
        };

        private static CellEdge NearestEdge(GdsPoint point, GdsBoundingBox bbox)
        {
            var best = CellEdge.Left;
            double bestDistance = Math.Abs(point.X - bbox.MinX);

            double top = Math.Abs(bbox.MaxY - point.Y);
            if (top < bestDistance) { best = CellEdge.Top; bestDistance = top; }

            double right = Math.Abs(bbox.MaxX - point.X);
            if (right < bestDistance) { best = CellEdge.Right; bestDistance = right; }

            double bottom = Math.Abs(point.Y - bbox.MinY);
            if (bottom < bestDistance) { best = CellEdge.Bottom; }

            return best;
        }

        private static AnchorGeometry? ProbeAnchorGeometry(
            GdsPoint anchor, IReadOnlyList<GdsPolygon> polygons, GdsPinDetectionOptions options)
        {
            double toleranceSquared = options.LabelGeometryTouchToleranceUm * options.LabelGeometryTouchToleranceUm;
            double bestDistanceSquared = double.PositiveInfinity;
            GdsPolygon? bestPolygon = null;
            GdsPoint bestP1 = default, bestP2 = default;
            bool touchesWaveguide = false, touchesMetal = false;

            foreach (var polygon in polygons)
            {
                bool isMetal = ContainsLayer(
                    options.ElectricalLayers ?? GdsPinDetectionOptions.LunimaElectricalLayers,
                    polygon.Layer, polygon.DataType);
                bool isWaveguide = !isMetal && ContainsLayer(options.WaveguideLayers, polygon.Layer, polygon.DataType);
                if (!isMetal && !isWaveguide)
                    continue;

                double polygonBestSquared = double.PositiveInfinity;
                GdsPoint polygonBestP1 = default, polygonBestP2 = default;
                foreach (var (p1, p2) in Segments(polygon))
                {
                    if (p1.Equals(p2))
                        continue;
                    double distanceSquared = DistanceToSegmentSquared(anchor, p1, p2);
                    if (distanceSquared < polygonBestSquared)
                    {
                        polygonBestSquared = distanceSquared;
                        polygonBestP1 = p1;
                        polygonBestP2 = p2;
                    }
                }
                if (polygonBestSquared == double.PositiveInfinity)
                    continue;

                if (polygonBestSquared <= toleranceSquared || PointInPolygon(polygon.Points, anchor))
                {
                    touchesMetal |= isMetal;
                    touchesWaveguide |= isWaveguide;
                }
                if (polygonBestSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = polygonBestSquared;
                    bestPolygon = polygon;
                    bestP1 = polygonBestP1;
                    bestP2 = polygonBestP2;
                }
            }

            return bestPolygon is null
                ? null
                : new AnchorGeometry(bestDistanceSquared, bestPolygon, bestP1, bestP2, touchesWaveguide, touchesMetal);
        }

        private static double SegmentOutwardAngleDegrees(GdsPolygon polygon, GdsPoint p1, GdsPoint p2)
        {
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / length;
            double ny = dx / length;
            var probe = new GdsPoint(
                ((p1.X + p2.X) / 2.0) + (nx * InteriorProbeOffsetUm),
                ((p1.Y + p2.Y) / 2.0) + (ny * InteriorProbeOffsetUm));
            if (PointInPolygon(polygon.Points, probe))
            {
                nx = -nx;
                ny = -ny;
            }
            return GdsInstancePinProjector.Normalize360(Math.Atan2(-ny, nx) * 180.0 / Math.PI);
        }

        private static bool? InferLabelPinKind(string label, AnchorGeometry? geometry)
        {
            if (geometry is not null)
            {
                if (geometry.TouchesMetal)
                    return true;
                if (geometry.TouchesWaveguide)
                    return null;
            }

            foreach (string marker in ElectricalLabelMarkers)
            {
                if (label.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return null;
        }

        private static bool PointInPolygon(IReadOnlyList<GdsPoint> polygon, GdsPoint point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                if ((pi.Y > point.Y) != (pj.Y > point.Y)
                    && point.X < ((pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToSegmentSquared(GdsPoint point, GdsPoint a, GdsPoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared == 0
                ? 0
                : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
            double cx = a.X + (t * dx) - point.X;
            double cy = a.Y + (t * dy) - point.Y;
            return (cx * cx) + (cy * cy);
        }

        private static CellEdge? TouchingEdge(GdsPoint p1, GdsPoint p2, GdsBoundingBox bbox, double tolerance)
        {
            if (Math.Abs(p1.X - bbox.MinX) <= tolerance && Math.Abs(p2.X - bbox.MinX) <= tolerance)
                return CellEdge.Left;
            if (Math.Abs(p1.Y - bbox.MaxY) <= tolerance && Math.Abs(p2.Y - bbox.MaxY) <= tolerance)
                return CellEdge.Top;
            if (Math.Abs(p1.X - bbox.MaxX) <= tolerance && Math.Abs(p2.X - bbox.MaxX) <= tolerance)
                return CellEdge.Right;
            if (Math.Abs(p1.Y - bbox.MinY) <= tolerance && Math.Abs(p2.Y - bbox.MinY) <= tolerance)
                return CellEdge.Bottom;
            return null;
        }

        private static IEnumerable<(GdsPoint P1, GdsPoint P2)> Segments(GdsPolygon polygon)
        {
            var points = polygon.Points;
            for (int i = 0; i + 1 < points.Count; i++)
                yield return (points[i], points[i + 1]);

            if (points.Count > 2 && !points[0].Equals(points[^1]))
                yield return (points[^1], points[0]);
        }

        private static List<(double Start, double End)> MergeIntervals(
            List<(double Start, double End)> intervals, double tolerance)
        {
            intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<(double Start, double End)>();
            foreach (var (start, end) in intervals)
            {
                if (merged.Count > 0 && start <= merged[^1].End + tolerance)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                else
                    merged.Add((start, end));
            }
            return merged;
        }

        private static GdsPoint MidpointOnEdge(CellEdge edge, double along, GdsBoundingBox bbox) => edge switch
        {
            CellEdge.Left => new GdsPoint(bbox.MinX, along),
            CellEdge.Right => new GdsPoint(bbox.MaxX, along),
            CellEdge.Top => new GdsPoint(along, bbox.MaxY),
            CellEdge.Bottom => new GdsPoint(along, bbox.MinY),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown cell edge."),
        };

        private static bool IsCoveredByLabel(GdsPoint midpoint, List<GdsPoint> labelAnchors, double tolerance)
        {
            foreach (var anchor in labelAnchors)
            {
                double dx = anchor.X - midpoint.X;
                double dy = anchor.Y - midpoint.Y;
                if (dx * dx + dy * dy <= tolerance * tolerance)
                    return true;
            }
            return false;
        }

        private static bool ContainsLayer(
            IReadOnlyList<(int Layer, int Datatype)> layers, int layer, int datatype)
        {
            foreach (var (l, d) in layers)
            {
                if (l == layer && d == datatype)
                    return true;
            }
            return false;
        }
    }
}

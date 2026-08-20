using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.PinKinds;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsCellDraftMapper"/>: GDS cell draft → PDK component
/// draft mapping (name sanitization, pins, black-box S-matrix, raw-code token
/// substitution, outline mapping).
/// </summary>
public class GdsCellDraftMapperTests
{
    private const double Tolerance = 1e-9;

    private static GdsCellDraft Draft(string cellName = "wg") => new()
    {
        CellName = cellName,
        WidthUm = 10,
        HeightUm = 4,
        Pins = new[]
        {
            new DetectedPin { Name = "in", XUm = 0, YUm = 2, AngleDegrees = 180, Source = DetectedPinSource.Label },
            new DetectedPin { Name = "out", XUm = 10, YUm = 2, AngleDegrees = 0, Source = DetectedPinSource.Label },
        },
        RawCode = $"def component():\n    return nd.load_gds(filename=\"{GdsHierarchyImporter.GdsFileNameToken}\", cellname=\"wg\")\n",
    };

    [Theory]
    [InlineData("wgA", "wgA")]
    [InlineData("my cell", "my_cell")]
    [InlineData("cell\"quoted\"", "cell_quoted_")]
    [InlineData("weird/slash\\back", "weird_slash_back")]
    [InlineData("cell#1(2)", "cell_1_2_")]
    [InlineData("mzi.v2-final", "mzi.v2-final")]
    [InlineData("", "gds_cell")]
    public void SanitizeComponentName_ReplacesInvalidCharactersDeterministically(string cellName, string expected) =>
        GdsCellDraftMapper.SanitizeComponentName(cellName).ShouldBe(expected);

    [Fact]
    public void Map_SetsDimensionsCategoryAndBlackBoxDefaults()
    {
        var result = GdsCellDraftMapper.Map(Draft("wgA"), "/tmp/lib/circuit.gds");

        result.Name.ShouldBe("wgA");
        result.Category.ShouldBe(GdsCellDraftMapper.ImportCategory);
        result.WidthMicrometers.ShouldBe(10, Tolerance);
        result.HeightMicrometers.ShouldBe(4, Tolerance);
        result.SMatrix.ShouldBeNull("GDS imports are black boxes — no simulation model");
        result.RawCodeBackend.ShouldBe("nazca");
        result.NazcaFunction.ShouldBeNull("raw-code components carry no nazca function (mirrors CustomComponentDraftFactory)");
    }

    [Fact]
    public void Map_MapsPinsToPhysicalPinDrafts()
    {
        var result = GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds");

        result.Pins.Count.ShouldBe(2);
        result.Pins[0].Name.ShouldBe("in");
        result.Pins[0].OffsetXMicrometers.ShouldBe(0, Tolerance);
        result.Pins[0].OffsetYMicrometers.ShouldBe(2, Tolerance);
        result.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        result.Pins[1].Name.ShouldBe("out");
        result.Pins[1].OffsetXMicrometers.ShouldBe(10, Tolerance);
        result.Pins[1].AngleDegrees.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void Map_SubstitutesGdsFileNameToken_WithPythonEscapedAbsolutePath()
    {
        var result = GdsCellDraftMapper.Map(Draft(), @"C:\Users\u\user-pdks\circuit.gds");

        result.RawCode.ShouldNotContain(GdsHierarchyImporter.GdsFileNameToken);
        result.RawCode.ShouldContain(@"filename=""C:\\Users\\u\\user-pdks\\circuit.gds""");
        result.RawCode.ShouldContain("cellname=\"wg\"");
    }

    [Fact]
    public void Map_MapsOutlinesFieldByField()
    {
        var draft = Draft();
        draft = draft with
        {
            Outlines = new[]
            {
                new GdsOutlinePolygon
                {
                    Layer = 1,
                    DataType = 0,
                    Points = new[]
                    {
                        new GdsOutlinePoint(0, 1.75), new GdsOutlinePoint(10, 1.75),
                        new GdsOutlinePoint(10, 2.25), new GdsOutlinePoint(0, 2.25),
                        new GdsOutlinePoint(0, 1.75), // closed ring: first point repeated
                    },
                },
            },
        };

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds");

        var polygon = result.OutlinePolygons.ShouldHaveSingleItem();
        polygon.Layer.ShouldBe(1);
        polygon.DataType.ShouldBe(0);
        polygon.Points.Count.ShouldBe(5);
        polygon.Points[1].X.ShouldBe(10, Tolerance);
        polygon.Points[1].Y.ShouldBe(1.75, Tolerance);
        polygon.Points[3].X.ShouldBe(0, Tolerance);
        polygon.Points[3].Y.ShouldBe(2.25, Tolerance);
        polygon.Points[4].ShouldBe(polygon.Points[0], "the closed ring must survive the mapping");
    }

    [Fact]
    public void Map_NoOutlines_LeavesOutlinePolygonsNull() =>
        GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds")
            .OutlinePolygons.ShouldBeNull("null keeps the rectangle-rendering fallback");

    // ── Pin-name normalization ───────────────────────────────────────────────

    [Fact]
    public void Map_BlankPinName_RenamedToPinNWithWarning()
    {
        // An empty label text is legal GDS — but the PDK loader rejects blank
        // pin names, so the pin must never reach the persisted file as-is.
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin { Name = "out", XUm = 10, YUm = 2, AngleDegrees = 0, Source = DetectedPinSource.Label },
                new DetectedPin { Name = "", XUm = 0, YUm = 2, AngleDegrees = 180, Source = DetectedPinSource.Label },
            },
        };
        var warnings = new List<string>();

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds", warnings);

        result.Pins.Select(p => p.Name).ShouldBe(new[] { "out", "pin_1" });
        warnings.ShouldHaveSingleItem().ShouldContain("pin_1");
    }

    [Fact]
    public void Map_DuplicatePinNames_DedupedDeterministicallyWithWarning()
    {
        // Two labels with the same text (legal GDS): connections resolve pins
        // by name, so the later pin gets a deterministic _2 suffix.
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin { Name = "o1", XUm = 0, YUm = 1, AngleDegrees = 180, Source = DetectedPinSource.Label },
                new DetectedPin { Name = "o1", XUm = 0, YUm = 3, AngleDegrees = 180, Source = DetectedPinSource.Label },
            },
        };
        var warnings = new List<string>();

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds", warnings);

        result.Pins.Select(p => p.Name).ShouldBe(new[] { "o1", "o1_2" });
        warnings.ShouldHaveSingleItem().ShouldContain("o1_2");
    }

    [Fact]
    public void Map_HeuristicNameCollidingWithLabel_DedupedDistinct()
    {
        // A label literally named "heur_1" colliding with the heuristic pin of
        // the same name — both survive with distinct names.
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin { Name = "heur_1", XUm = 0, YUm = 2, AngleDegrees = 180, Source = DetectedPinSource.EdgeHeuristic },
                new DetectedPin { Name = "heur_1", XUm = 10, YUm = 2, AngleDegrees = 0, Source = DetectedPinSource.Label },
            },
        };
        var warnings = new List<string>();

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds", warnings);

        result.Pins.Select(p => p.Name).ShouldBe(new[] { "heur_1", "heur_1_2" });
        warnings.ShouldHaveSingleItem().ShouldContain("heur_1_2");
    }

    [Fact]
    public void Map_CleanPinNames_UntouchedAndNoWarnings()
    {
        var warnings = new List<string>();

        var result = GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds", warnings);

        result.Pins.Select(p => p.Name).ShouldBe(new[] { "in", "out" });
        warnings.ShouldBeEmpty();
    }

    // ── Pin kinds ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_ProvenElectricalPin_WritesElectricalPinKind_UnknownStaysAbsent()
    {
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin
                {
                    Name = "anode", XUm = 0, YUm = 2, AngleDegrees = 180,
                    Source = DetectedPinSource.Label, IsElectrical = true,
                },
                new DetectedPin
                {
                    Name = "o1", XUm = 10, YUm = 2, AngleDegrees = 0,
                    Source = DetectedPinSource.Label, IsElectrical = null,
                },
            },
        };

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds");

        result.Pins[0].PinKind.ShouldBe("Electrical");
        result.Pins[1].PinKind.ShouldBeNull("unknown kinds stay absent — the PDK loader reads them as the optical default");
    }

    [Fact]
    public void Map_ProvenElectricalPin_PlacedComponentPinIsElectrical()
    {
        // The whole survival chain: DetectedPin.IsElectrical → pinKind JSON field
        // → template pin MatterType → placed component's physical pin.
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin
                {
                    Name = "anode", XUm = 0, YUm = 2, AngleDegrees = 180,
                    Source = DetectedPinSource.Label, IsElectrical = true,
                },
                new DetectedPin
                {
                    Name = "o1", XUm = 10, YUm = 2, AngleDegrees = 0,
                    Source = DetectedPinSource.Label, IsElectrical = null,
                },
            },
        };

        var componentDraft = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds");
        var template = PdkTemplateConverter.ConvertToTemplate(componentDraft, "user-pdk", null);
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

        PinKindHelper.IsElectrical(component.PhysicalPins.Single(p => p.Name == "anode")).ShouldBeTrue();
        PinKindHelper.IsElectrical(component.PhysicalPins.Single(p => p.Name == "o1")).ShouldBeFalse();
    }

    [Fact]
    public void Map_TwoOpticalPins_PlacedComponentPassesLightThrough()
    {
        // A GDS-imported 2-pin cell must not absorb all light — the placed
        // component's S-matrix carries the lossless in↔out pass-through.
        var componentDraft = GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds");
        var template = PdkTemplateConverter.ConvertToTemplate(componentDraft, "user-pdk", null);
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

        var matrix = component.WaveLengthToSMatrixMap.Values.First();
        var inPin = component.PhysicalPins.Select(p => p.LogicalPin!).Single(p => p.Name == "in");
        var outPin = component.PhysicalPins.Select(p => p.LogicalPin!).Single(p => p.Name == "out");

        var transfers = matrix.GetNonNullValues();
        transfers.Count.ShouldBe(2);
        transfers[(inPin.IDInFlow, outPin.IDOutFlow)].Magnitude.ShouldBe(1.0, Tolerance);
        transfers[(outPin.IDInFlow, inPin.IDOutFlow)].Magnitude.ShouldBe(1.0, Tolerance);
    }

    // ── Pin waveguide width / layer (DRC-lite) ───────────────────────────────

    [Fact]
    public void Map_StampsDetectedPortLayerOnPins()
    {
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin
                {
                    Name = "o1", XUm = 0, YUm = 2, AngleDegrees = 180,
                    Source = DetectedPinSource.Label, Layer = 1,
                },
                new DetectedPin
                {
                    Name = "o2", XUm = 10, YUm = 2, AngleDegrees = 0,
                    Source = DetectedPinSource.Label, Layer = null,
                },
            },
        };

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds");

        result.Pins[0].Layer.ShouldBe(1, "the detected port layer feeds the DRC-lite layer rule");
        result.Pins[1].Layer.ShouldBeNull("pins without an attributable layer stay null");
    }

    [Fact]
    public void Map_ProcessDefaultWidth_StampsOpticalPinsOnly()
    {
        var draft = Draft() with
        {
            Pins = new[]
            {
                new DetectedPin
                {
                    Name = "anode", XUm = 0, YUm = 2, AngleDegrees = 180,
                    Source = DetectedPinSource.Label, IsElectrical = true,
                },
                new DetectedPin
                {
                    Name = "o1", XUm = 10, YUm = 2, AngleDegrees = 0,
                    Source = DetectedPinSource.Label, IsElectrical = null,
                },
            },
        };

        var result = GdsCellDraftMapper.Map(draft, "/tmp/lib/circuit.gds", processDefaultWidthUm: 0.5);

        result.Pins[0].WaveguideWidthMicrometers.ShouldBeNull("electrical pins carry no optical width");
        result.Pins[1].WaveguideWidthMicrometers.ShouldBe(0.5);
    }

    [Fact]
    public void Map_NoProcessDefaultWidth_KeepsWidthNull()
    {
        var result = GdsCellDraftMapper.Map(Draft(), "/tmp/lib/circuit.gds");

        result.Pins.ShouldAllBe(p => p.WaveguideWidthMicrometers == null,
            "without process data the width stays null and the mismatch rule stays silent");
    }
}

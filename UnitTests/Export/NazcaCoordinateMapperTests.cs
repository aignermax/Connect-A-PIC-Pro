using CAP_Core.Components.Core;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Pure-math unit tests for <see cref="NazcaCoordinateMapper"/>: placement formula for
/// every bbox parameterisation, rotation handling, pin conversion and detection rules.
/// </summary>
public class NazcaCoordinateMapperTests
{
    private const double Tolerance = 1e-9;

    /// <summary>
    /// Creates a bare component at app top-left (x, y) with the given UNROTATED size,
    /// then applies <paramref name="rotationSteps"/> app rotations via <see cref="RotateLikeApp"/>.
    /// </summary>
    private static Component CreateComponent(
        double x, double y, double w0, double h0,
        string nazcaFunctionName = "placeCell_StraightWG", int rotationSteps = 0)
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = x;
        comp.PhysicalY = y;
        comp.WidthMicrometers = w0;
        comp.HeightMicrometers = h0;
        comp.NazcaFunctionName = nazcaFunctionName;
        for (int i = 0; i < rotationSteps; i++)
            RotateLikeApp(comp);
        return comp;
    }

    /// <summary>
    /// Mirrors the app's RotateComponentCommand semantics: pin offsets rotate 90° CCW about
    /// the box centre, width/height swap, top-left stays fixed, RotationDegrees += 90.
    /// </summary>
    private static void RotateLikeApp(Component comp)
    {
        double w = comp.WidthMicrometers;
        double h = comp.HeightMicrometers;
        foreach (var pin in comp.PhysicalPins)
        {
            double x = pin.OffsetXMicrometers - w / 2;
            double y = pin.OffsetYMicrometers - h / 2;
            pin.OffsetXMicrometers = -y + h / 2;
            pin.OffsetYMicrometers = x + w / 2;
        }
        comp.WidthMicrometers = h;
        comp.HeightMicrometers = w;
        comp.RotationDegrees = (comp.RotationDegrees + 90) % 360;
    }

    // Override anchor XMin=-3, YMax=10 with unrotated W0=45, H0=11 gives the cell-internal
    // bbox B = [-3, -1, 42, 10] (Nazca Y-up). Put rotation r = -RotationDegrees rotates B's
    // four corners; put position T = (PhysX - minx', -PhysY - maxy'), box at (100, 50):
    //  r=0:    B unchanged                                       -> minx'=-3,  maxy'=10 -> T=(103, -60)
    //  r=-90:  (x,y)->(y,-x): (-1,3) (-1,-42) (10,-42) (10,3)    -> minx'=-1,  maxy'=3  -> T=(101, -53)
    //  r=-180: (x,y)->(-x,-y): (3,1) (-42,1) (-42,-10) (3,-10)   -> minx'=-42, maxy'=1  -> T=(142, -51)
    //  r=-270: (x,y)->(-y,x): (1,-3) (1,42) (-10,42) (-10,-3)    -> minx'=-10, maxy'=42 -> T=(110, -92)
    [Theory]
    [InlineData(0, 103, -60, 0)]
    [InlineData(1, 101, -53, -90)]
    [InlineData(2, 142, -51, -180)]
    [InlineData(3, 110, -92, -270)]
    public void GetCellPlacement_OverrideAnchor_AllRotations(
        int rotationSteps, double expectedX, double expectedY, double expectedRotation)
    {
        var comp = CreateComponent(100, 50, w0: 45, h0: 11, rotationSteps: rotationSteps);

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, (-3, 10));

        placement.X.ShouldBe(expectedX, Tolerance);
        placement.Y.ShouldBe(expectedY, Tolerance);
        placement.RotationDegrees.ShouldBe(expectedRotation, Tolerance);
    }

    // PDK calibration offset (ox=0, oy=30) with W0=250, H0=60 parameterises
    // B = [-ox, oy-H0, -ox+W0, oy] = [0, -30, 250, 30]; component box at (100, 50),
    // T = (PhysX - minx', -PhysY - maxy'). Hand-derived per put rotation:
    //  r=0:    B unchanged    -> minx'=0,    maxy'=30  -> T=(100, -80) — must equal the
    //          calibrated legacy convention org = (PhysX + ox, -(PhysY + oy)).
    //  r=-90:  (x,y)->(y,-x)  -> minx'=-30,  maxy'=0   -> T=(130, -50)
    //  r=-180: (x,y)->(-x,-y) -> minx'=-250, maxy'=30  -> T=(350, -80)
    //  r=-270: (x,y)->(-y,x)  -> minx'=-30,  maxy'=250 -> T=(130, -300)
    [Theory]
    [InlineData(0, 100, -80, 0)]
    [InlineData(1, 130, -50, -90)]
    [InlineData(2, 350, -80, -180)]
    [InlineData(3, 130, -300, -270)]
    public void GetCellPlacement_PdkExplicitOffset_AllRotations(
        int rotationSteps, double expectedX, double expectedY, double expectedRotation)
    {
        var comp = CreateComponent(100, 50, 250, 60, "demo.mmi2x2_dp", rotationSteps);
        comp.NazcaOriginOffsetX = 0;
        comp.NazcaOriginOffsetY = 30;

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(expectedX, Tolerance);
        placement.Y.ShouldBe(expectedY, Tolerance);
        placement.RotationDegrees.ShouldBe(expectedRotation, Tolerance);
    }

    [Fact]
    public void GetCellPlacement_LegacyFallback_AnchorsOrgAtBoxBottomLeft()
    {
        // No PDK name, no explicit offset, no parametric straight -> (ox, oy) = (0, H0),
        // i.e. B = [0, 0, W0, H0]; at rot 0: T = (PhysX, -(PhysY + H0)) = (10, -270).
        var comp = CreateComponent(10, 20, 250, 250);

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(10, Tolerance);
        placement.Y.ShouldBe(-270, Tolerance);
        placement.RotationDegrees.ShouldBe(0);
    }

    [Fact]
    public void GetCellPlacement_ParametricStraight_UsesFirstPinOffsetAsOrigin()
    {
        // Parametric straights anchor org on the first pin: (ox, oy) = (0, 125), H0 = 250
        // -> B = [0, -125, 250, 125]; at rot 0: T = (10 - 0, -20 - 125) = (10, -145).
        var comp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        comp.PhysicalX = 10;
        comp.PhysicalY = 20;
        comp.WidthMicrometers = 250;
        comp.HeightMicrometers = 250;
        comp.NazcaFunctionParameters = "length=250";

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(10, Tolerance);
        placement.Y.ShouldBe(-145, Tolerance);
    }

    [Fact]
    public void GetCellPlacement_NullAnchorWithPdkName_TakesCalibrationPathEvenWithZeroOffset()
    {
        // PDK detection must NOT fall through to the legacy (0, H0) fallback when the
        // calibrated offset happens to be (0, 0): B = [0, -60, 250, 0] -> T = (100, -50);
        // the legacy fallback would give (100, -110) instead.
        var comp = CreateComponent(100, 50, 250, 60, "demo.mmi2x2_dp");

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(100, Tolerance);
        placement.Y.ShouldBe(-50, Tolerance);
    }

    [Fact]
    public void GetCellPlacement_ExplicitOffsetWithoutPdkName_TakesCalibrationPath()
    {
        // Auto-named components with a calibrated offset (grating couplers, issue #355)
        // must use it: ox=15, oy=30, W0=H0=30 -> B = [-15, 0, 15, 30];
        // at rot 0: T = (0 + 15, -0 - 30) = (15, -30).
        var comp = CreateComponent(0, 0, 30, 30, "nazca_grating_coupler");
        comp.NazcaOriginOffsetX = 15;
        comp.NazcaOriginOffsetY = 30;

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, null);

        placement.X.ShouldBe(15, Tolerance);
        placement.Y.ShouldBe(-30, Tolerance);
    }

    [Fact]
    public void GetPinNazcaPosition_IsPlainYNegationOfAppPosition()
    {
        // App pin world position (100+10, 50+20) = (110, 70) -> Nazca (110, -70).
        var comp = CreateComponent(100, 50, 45, 11);
        var pin = new PhysicalPin
        { Name = "p", ParentComponent = comp, OffsetXMicrometers = 10, OffsetYMicrometers = 20 };

        var (x, y) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);

        x.ShouldBe(110, Tolerance);
        y.ShouldBe(-70, Tolerance);
    }

    [Fact]
    public void GetCellPlacement_DemoShallowBend_RenderedPinsCoincideWithAppPins()
    {
        // Regression for #635: the demofab 90° bend (demo.shallow.bend, angle=90) has a real
        // cell — verified against nazca 0.6.1 — with org == a0 at cell (0,0) and b0 at cell
        // (200,200); its bbox is [0,-9.4,209.4,200]. The org offset above the box top is
        // therefore ymax = 200 (NOT |ymin| = 9.4, the value that placed the bend 190.6 µm
        // too high). With the corrected data the org-placed cell's pins must land exactly on
        // the app pins.
        var comp = CreateComponent(30, 20, 209.4, 209.4, nazcaFunctionName: "demo.shallow.bend");
        comp.PhysicalPins.Clear();
        comp.NazcaOriginOffsetX = 0;
        comp.NazcaOriginOffsetY = 200;
        comp.PhysicalPins.Add(new PhysicalPin
        { Name = "a0", ParentComponent = comp, OffsetXMicrometers = 0, OffsetYMicrometers = 200, AngleDegrees = 180 });
        comp.PhysicalPins.Add(new PhysicalPin
        { Name = "b0", ParentComponent = comp, OffsetXMicrometers = 200, OffsetYMicrometers = 0, AngleDegrees = 270 });

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);

        // Real cell: a0 is org, b0 sits at org + (200, 200).
        var (a0x, a0y) = NazcaCoordinateMapper.GetPinNazcaPosition(comp.PhysicalPins[0]);
        (placement.X + 0).ShouldBe(a0x, Tolerance);
        (placement.Y + 0).ShouldBe(a0y, Tolerance);

        var (b0x, b0y) = NazcaCoordinateMapper.GetPinNazcaPosition(comp.PhysicalPins[1]);
        (placement.X + 200).ShouldBe(b0x, Tolerance);
        (placement.Y + 200).ShouldBe(b0y, Tolerance);
    }

    [Fact]
    public void GetPinNazcaPosition_RotatedComponent_UsesPreRotatedOffsets()
    {
        // The app pre-rotates pin offsets about the box centre: pin (0, 5.5) on a 45x11 box
        // has centre distance (-22.5, 0); CCW rotation gives (0, -22.5), re-anchored on the
        // swapped centre (5.5, 22.5) -> offset (5.5, 0); world (105.5, 50) -> Nazca (105.5, -50).
        var comp = CreateComponent(100, 50, 45, 11);
        comp.PhysicalPins.Add(new PhysicalPin
        { Name = "p", ParentComponent = comp, OffsetXMicrometers = 0, OffsetYMicrometers = 5.5 });
        RotateLikeApp(comp);

        var (x, y) = NazcaCoordinateMapper.GetPinNazcaPosition(comp.PhysicalPins[^1]);

        x.ShouldBe(105.5, Tolerance);
        y.ShouldBe(-50, Tolerance);
    }

    // Nazca pin angle = -(AngleDegrees + RotationDegrees normalised to [0, 360)) — same
    // convention as "-pin.GetAbsoluteAngle()" in the exporter, so scripts stay byte-identical.
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(180, 0, -180)]
    [InlineData(180, 90, -270)]   // world 270 -> -270
    [InlineData(270, 180, -90)]   // world 450 normalises to 90 -> -90
    public void GetPinNazcaAngle_NegatesNormalisedWorldAngle(
        double pinAngle, double rotationDegrees, double expected)
    {
        var comp = CreateComponent(0, 0, 10, 10);
        comp.RotationDegrees = rotationDegrees;

        NazcaCoordinateMapper.GetPinNazcaAngle(
            new PhysicalPin { Name = "p", ParentComponent = comp, AngleDegrees = pinAngle })
            .ShouldBe(expected, Tolerance);
    }

    [Fact]
    public void ToNazca_NegatesY_AndAvoidsNegativeZero()
    {
        NazcaCoordinateMapper.ToNazca(5, 7).ShouldBe((5d, -7d));
        var (_, y) = NazcaCoordinateMapper.ToNazca(3, 0);
        double.IsNegative(y).ShouldBeFalse();
    }

    // Detection rules must stay behaviour-identical to the exporter heuristics they
    // replace: known SiEPIC prefixes, module dot-notation (except demo_pdk stubs).
    [Theory]
    [InlineData("ebeam_y_1550", true)]
    [InlineData("GC_TE_1550_8degOxide_BB", true)]
    [InlineData("ANT_MMI_1x2", true)]
    [InlineData("crossing_horizontal", true)]
    [InlineData("taper_350nm", true)]
    [InlineData("contra_dc", true)]
    [InlineData("demo.mmi2x2_dp", true)]
    [InlineData("demo_pdk.splitter", false)]
    [InlineData("placeCell_StraightWG", false)]
    public void IsPdkFunction_MatchesExporterHeuristic(string name, bool expected) =>
        NazcaCoordinateMapper.IsPdkFunction(name).ShouldBe(expected);

    // Parametric straight = name contains "straight" or "strt" AND parameters carry a
    // length argument; the check is case-insensitive on the length key.
    [Theory]
    [InlineData("cell_straight", "length=100", true)]
    [InlineData("my_strt", "Length=5", true)]
    [InlineData("cell_straight", "", false)]
    [InlineData("mmi2x2", "length=100", false)]
    public void IsParametricStraight_MatchesExporterHeuristic(
        string funcName, string parameters, bool expected) =>
        NazcaCoordinateMapper.IsParametricStraight(funcName, parameters).ShouldBe(expected);
}

using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;

namespace UnitTests.Routing;

/// <summary>
/// Builds the component blocks of the routing repro designs
/// (overlappingwaveguides.lun / Kreisverbindung.lun): MZI-shaped blocks with
/// right-edge pins o2/o3 and a Taper with a left-edge pin, matching the
/// CornerStone SiN geometry of the original files. Also provides geometric
/// helpers to sample routed paths and measure the distance between them.
/// </summary>
public static class Issue704ReproCircuit
{
    /// <summary>MZI block width so the right-edge pins land at the repro coordinates.</summary>
    public const double MziWidth = 616.8;

    /// <summary>MZI block height (pins o2/o3 sit at 62.6 / 68.5 from the top).</summary>
    public const double MziHeight = 137.0;

    /// <summary>Vertical pin offset of MZI pin o2 (µm from component top).</summary>
    public const double MziPinO2OffsetY = 62.6;

    /// <summary>Vertical pin offset of MZI pin o3 (µm from component top).</summary>
    public const double MziPinO3OffsetY = 68.5;

    /// <summary>Sampling step along segments when flattening a path (µm).</summary>
    private const double SampleStepMicrometers = 1.0;

    private const int WavelengthNm = 1550;

    /// <summary>Creates an MZI-shaped block with pins o2 and o3 on its right edge.</summary>
    public static Component CreateMzi(string name, double x, double y) =>
        CreateBlock(name, x, y, MziWidth, MziHeight,
            ("o2", MziWidth, MziPinO2OffsetY, 0),
            ("o3", MziWidth, MziPinO3OffsetY, 0));

    /// <summary>Creates a Taper block whose left-edge pin o1 sits at the given absolute position.</summary>
    public static Component CreateTaper(string name, double pinX, double pinY) =>
        CreateBlock(name, pinX, pinY - 0.6, 46.0, 1.2, ("o1", 0, 0.6, 180));

    /// <summary>Width of the bundled cspdk.sin300.coupler_straight template (µm).</summary>
    public const double CouplerStraightWidth = 20.0;

    /// <summary>Height of the bundled cspdk.sin300.coupler_straight template (µm).</summary>
    public const double CouplerStraightHeight = 2.636;

    /// <summary>
    /// Creates a component with the exact pin geometry of the bundled
    /// cspdk.sin300.coupler_straight template: left-edge pins o1/o2 facing west,
    /// right-edge pins o3/o4 facing east. Origin (<paramref name="x"/>,<paramref name="y"/>)
    /// is the component's placement position, matching an exported netlist placement.
    /// </summary>
    public static Component CreateCouplerStraight(string name, double x, double y) =>
        CreateBlock(name, x, y, CouplerStraightWidth, CouplerStraightHeight,
            ("o1", 0.0, 2.036, 180),
            ("o2", 0.0, 0.6, 180),
            ("o4", CouplerStraightWidth, 2.036, 0),
            ("o3", CouplerStraightWidth, 0.6, 0));

    /// <summary>Finds a physical pin by name.</summary>
    public static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.Single(p => p.Name == name);

    /// <summary>
    /// Creates a rectangular block component with the given physical pins
    /// (offsets relative to the component origin, angle = facing direction).
    /// </summary>
    public static Component CreateBlock(
        string name, double x, double y, double width, double height,
        params (string Name, double OffsetX, double OffsetY, double Angle)[] pinDefs)
    {
        var logicalPins = new List<Pin>();
        var physicalPins = new List<PhysicalPin>();
        for (int i = 0; i < pinDefs.Length; i++)
        {
            var def = pinDefs[i];
            var logical = new Pin(def.Name, i, MatterType.Light, RectSide.Right);
            logicalPins.Add(logical);
            physicalPins.Add(new PhysicalPin
            {
                Name = def.Name,
                OffsetXMicrometers = def.OffsetX,
                OffsetYMicrometers = def.OffsetY,
                AngleDegrees = def.Angle,
                LogicalPin = logical,
            });
        }

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(logicalPins);
        var pinIds = logicalPins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sMatrix = new SMatrix(pinIds, new());

        return new Component(
            new Dictionary<int, SMatrix> { { WavelengthNm, sMatrix } },
            new List<Slider>(), "block", "", parts, 0, name,
            DiscreteRotation.R0, physicalPins)
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = width,
            HeightMicrometers = height,
        };
    }

    /// <summary>
    /// Flattens a routed path into points sampled every µm along straights and bends.
    /// </summary>
    public static List<(double X, double Y)> SamplePath(RoutedPath path)
    {
        var points = new List<(double, double)>();
        foreach (var segment in path.Segments)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(segment.LengthMicrometers / SampleStepMicrometers));
            for (int i = 0; i <= steps; i++)
                points.Add(PointOnSegment(segment, (double)i / steps));
        }
        return points;
    }

    /// <summary>Minimum distance between two sampled paths (µm).</summary>
    public static double MinDistanceBetween(
        List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        double minSquared = double.MaxValue;
        foreach (var p in a)
        {
            foreach (var q in b)
            {
                double dx = p.X - q.X;
                double dy = p.Y - q.Y;
                double squared = dx * dx + dy * dy;
                if (squared < minSquared) minSquared = squared;
            }
        }
        return Math.Sqrt(minSquared);
    }

    private static (double X, double Y) PointOnSegment(PathSegment segment, double t)
    {
        if (segment is BendSegment bend)
        {
            double sign = Math.Sign(bend.SweepAngleDegrees);
            if (sign == 0) sign = 1;
            double angleRad = (bend.StartAngleDegrees + bend.SweepAngleDegrees * t) * Math.PI / 180;
            return (bend.Center.X + bend.RadiusMicrometers * Math.Cos(angleRad - Math.PI / 2 * sign),
                    bend.Center.Y + bend.RadiusMicrometers * Math.Sin(angleRad - Math.PI / 2 * sign));
        }

        return (segment.StartPoint.X + (segment.EndPoint.X - segment.StartPoint.X) * t,
                segment.StartPoint.Y + (segment.EndPoint.Y - segment.StartPoint.Y) * t);
    }
}

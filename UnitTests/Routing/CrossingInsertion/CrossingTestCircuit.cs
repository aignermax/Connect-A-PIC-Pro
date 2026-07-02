using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.CrossingInsertion;
using CAP_Core.Tiles;

namespace UnitTests.Routing.CrossingInsertion;

/// <summary>
/// Builds test circuits for adaptive crossing-insertion tests: four terminal
/// components forming a horizontal and a vertical net that intersect at (200, 100),
/// plus a factory replicating the PDK ebeam_crossing4 component (9.7 x 9.7 µm,
/// through 0.98, crosstalk 0.02, reflection 0.01).
/// </summary>
public static class CrossingTestCircuit
{
    /// <summary>Edge length of the test crossing component in micrometers (PDK value).</summary>
    public const double CrossingEdgeMicrometers = 9.7;

    /// <summary>Through-transmission magnitude of the crossing (PDK value).</summary>
    public const double ThroughMagnitude = 0.98;

    /// <summary>Crosstalk magnitude into the orthogonal arms (PDK value).</summary>
    public const double CrosstalkMagnitude = 0.02;

    /// <summary>Back-reflection magnitude at each port (PDK value).</summary>
    public const double ReflectionMagnitude = 0.01;

    private const int WavelengthNm = 1550;
    private const double TerminalSizeMicrometers = 10.0;

    /// <summary>One terminal component with a single optical pin.</summary>
    public record Terminal(Component Component, PhysicalPin PhysicalPin, Pin LogicalPin);

    /// <summary>A fully wired cross-shaped test layout.</summary>
    public record CrossLayout(
        WaveguideConnectionManager Manager,
        WaveguideRouter Router,
        CrossingInsertionService Service,
        List<Component> AddedCrossings,
        List<Component> RemovedCrossings,
        ComponentListTileManager TileManager,
        Terminal ALeft, Terminal ARight, Terminal BTop, Terminal BBottom);

    /// <summary>
    /// Builds the standard cross layout: horizontal net (10,100)→(390,100) and
    /// vertical net (200,50)→(200,350) that intersect at (200,100). The vertical
    /// net is added first, so the horizontal one must detour around it; the
    /// crossing pass then decides crossing vs. detour by insertion loss.
    /// </summary>
    /// <param name="bendLossDbPer90Deg">Bend loss to make the detour expensive or cheap.</param>
    public static CrossLayout Build(double bendLossDbPer90Deg)
    {
        // Only the light-source terminal maps InFlow→OutFlow; the sink terminals are
        // absorbing, otherwise the mirrors would form a Fabry–Pérot cavity through
        // the crossing and distort the measured through/crosstalk amplitudes.
        var aLeft = CreateTerminal("A_left", 0, 95, pinAngleDegrees: 0, sourceCoupling: 1.0);
        var aRight = CreateTerminal("A_right", 390, 95, pinAngleDegrees: 180);
        var bTop = CreateTerminal("B_top", 195, 40, pinAngleDegrees: 90);
        var bBottom = CreateTerminal("B_bottom", 195, 350, pinAngleDegrees: 270);

        var components = new List<Component>
        {
            aLeft.Component, aRight.Component, bTop.Component, bBottom.Component
        };

        var router = new WaveguideRouter { MinBendRadiusMicrometers = 10.0, AStarCellSize = 4.0 };
        router.InitializePathfindingGrid(0, 0, 400, 400, components);

        var tileManager = new ComponentListTileManager();
        foreach (var component in components)
            tileManager.AddComponent(component);

        var added = new List<Component>();
        var removed = new List<Component>();
        var service = new CrossingInsertionService(CreateCrossingComponent)
        {
            ComponentAdded = crossing =>
            {
                added.Add(crossing);
                tileManager.AddComponent(crossing);
            },
            ComponentRemoved = removed.Add,
        };

        var manager = new WaveguideConnectionManager(router)
        {
            DefaultBendLossDbPer90Deg = bendLossDbPer90Deg,
            CrossingInsertion = service,
        };

        manager.AddConnection(bTop.PhysicalPin, bBottom.PhysicalPin);
        manager.AddConnection(aLeft.PhysicalPin, aRight.PhysicalPin);

        return new CrossLayout(manager, router, service, added, removed, tileManager,
            aLeft, aRight, bTop, bBottom);
    }

    /// <summary>
    /// Creates a 10x10 µm terminal component with a single pin facing the given
    /// absolute angle (0 = east, 90 = south, 180 = west, 270 = north). With
    /// <paramref name="sourceCoupling"/> &gt; 0 the terminal maps InFlow→OutFlow
    /// (light source); otherwise it absorbs all incoming light (measurement sink).
    /// </summary>
    public static Terminal CreateTerminal(
        string name, double x, double y, double pinAngleDegrees, double sourceCoupling = 0.0)
    {
        var logicalPin = new Pin("opt", 0, MatterType.Light, RectSide.Right);
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin> { logicalPin });

        var (offsetX, offsetY) = PinOffsetForAngle(pinAngleDegrees);
        var physicalPin = new PhysicalPin
        {
            Name = "opt",
            OffsetXMicrometers = offsetX,
            OffsetYMicrometers = offsetY,
            AngleDegrees = pinAngleDegrees,
            LogicalPin = logicalPin,
        };

        var sMatrix = new SMatrix(new List<Guid> { logicalPin.IDInFlow, logicalPin.IDOutFlow }, new());
        if (sourceCoupling > 0)
        {
            sMatrix.SetValues(new Dictionary<(Guid, Guid), Complex>
            {
                { (logicalPin.IDInFlow, logicalPin.IDOutFlow), new Complex(sourceCoupling, 0) }
            });
        }

        var component = new Component(
            new Dictionary<int, SMatrix> { { WavelengthNm, sMatrix } },
            new List<Slider>(), "terminal", "", parts, 0, name,
            DiscreteRotation.R0, new List<PhysicalPin> { physicalPin })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = TerminalSizeMicrometers,
            HeightMicrometers = TerminalSizeMicrometers,
        };
        return new Terminal(component, physicalPin, logicalPin);
    }

    /// <summary>
    /// Creates a fresh crossing component replicating the PDK ebeam_crossing4:
    /// 9.7x9.7 µm, four ports (west/east/north/south), S-matrix with
    /// through 0.98 at -45°, crosstalk 0.02 and reflection 0.01 at 180°.
    /// </summary>
    public static Component CreateCrossingComponent()
    {
        var west = new Pin("port 1", 0, MatterType.Light, RectSide.Left);
        var east = new Pin("port 2", 1, MatterType.Light, RectSide.Right);
        var north = new Pin("port 3", 2, MatterType.Light, RectSide.Up);
        var south = new Pin("port 4", 3, MatterType.Light, RectSide.Down);
        var pins = new List<Pin> { west, east, north, south };

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(pins);

        double half = CrossingEdgeMicrometers / 2.0;
        var physicalPins = new List<PhysicalPin>
        {
            new() { Name = "port 1", OffsetXMicrometers = 0, OffsetYMicrometers = half, AngleDegrees = 180, LogicalPin = west },
            new() { Name = "port 2", OffsetXMicrometers = CrossingEdgeMicrometers, OffsetYMicrometers = half, AngleDegrees = 0, LogicalPin = east },
            new() { Name = "port 3", OffsetXMicrometers = half, OffsetYMicrometers = 0, AngleDegrees = 270, LogicalPin = north },
            new() { Name = "port 4", OffsetXMicrometers = half, OffsetYMicrometers = CrossingEdgeMicrometers, AngleDegrees = 90, LogicalPin = south },
        };

        var component = new Component(
            new Dictionary<int, SMatrix> { { WavelengthNm, CreateCrossingMatrix(pins) } },
            new List<Slider>(), "ebeam_crossing4", "", parts, 0, "Crossing 4-Port",
            DiscreteRotation.R0, physicalPins)
        {
            WidthMicrometers = CrossingEdgeMicrometers,
            HeightMicrometers = CrossingEdgeMicrometers,
        };
        return component;
    }

    /// <summary>
    /// Builds the 16-entry crossing S-matrix: through partner (west↔east,
    /// north↔south) 0.98 at -45°, orthogonal crosstalk 0.02, reflection 0.01 at 180°.
    /// </summary>
    private static SMatrix CreateCrossingMatrix(List<Pin> pins)
    {
        var through = Complex.FromPolarCoordinates(ThroughMagnitude, -Math.PI / 4.0);
        var crosstalk = new Complex(CrosstalkMagnitude, 0);
        var reflection = Complex.FromPolarCoordinates(ReflectionMagnitude, Math.PI);
        int[] throughPartner = { 1, 0, 3, 2 }; // west↔east, north↔south

        var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sMatrix = new SMatrix(pinIds, new());
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        for (int i = 0; i < pins.Count; i++)
        {
            for (int j = 0; j < pins.Count; j++)
            {
                var value = i == j ? reflection : j == throughPartner[i] ? through : crosstalk;
                transfers[(pins[i].IDInFlow, pins[j].IDOutFlow)] = value;
            }
        }
        sMatrix.SetValues(transfers);
        return sMatrix;
    }

    private static (double X, double Y) PinOffsetForAngle(double angleDegrees) => angleDegrees switch
    {
        0 => (TerminalSizeMicrometers, TerminalSizeMicrometers / 2.0),
        90 => (TerminalSizeMicrometers / 2.0, TerminalSizeMicrometers),
        180 => (0, TerminalSizeMicrometers / 2.0),
        270 => (TerminalSizeMicrometers / 2.0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(angleDegrees)),
    };
}

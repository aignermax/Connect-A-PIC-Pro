using CAP_Core.Routing;
using CAP_Core.Routing.AStarPathfinder;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Property assertion over the router fixture set: for every routed connection,
/// path segments must never intersect each other. A waveguide that crosses itself
/// (e.g. a full 360° loop at the start pin when the pin points away from the
/// target) has no optical model, so no fixture may produce one — regardless of
/// which routing engine (direct styled, A*, Manhattan fallback) built the path.
/// </summary>
public class RouterSelfIntersectionPropertyTests
{
    /// <summary>
    /// The router fixture set: deterministic layouts covering the pin-heading
    /// geometries a router must handle, including the loop-prone cases where one
    /// or both pins point away from their target.
    /// </summary>
    public static TheoryData<LayoutTestDefinition> RouterFixtures
    {
        get
        {
            var data = new TheoryData<LayoutTestDefinition>();
            data.Add(StraightFacing());
            data.Add(ParallelOffset());
            data.Add(Perpendicular());
            data.Add(OpposingUTurn());
            data.Add(PinsPointAwayFromEachOther());
            data.Add(VerticalFacing());
            data.Add(ChainOfTwoConnections());
            data.Add(TargetBehindAndAbove());
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RouterFixtures))]
    public void RoutedConnections_PathSegmentsDoNotIntersectEachOther(LayoutTestDefinition fixture)
    {
        var result = LayoutTestRunner.Run(fixture);

        result.ConnectionResults.ShouldNotBeEmpty(
            $"fixture '{fixture.Name}' must define at least one connection");
        foreach (var connection in result.ConnectionResults)
        {
            var path = connection.Path;
            path.ShouldNotBeNull(
                $"fixture '{fixture.Name}': {connection.Description} produced no path");
            path!.Segments.ShouldNotBeEmpty(
                $"fixture '{fixture.Name}': {connection.Description} produced an empty path");

            PathIntersectionDetector.HasSelfIntersection(path).ShouldBeFalse(
                $"fixture '{fixture.Name}': {connection.Description} crosses itself");

            if (path.DebugGridPath != null)
            {
                PathLoopDetector.IsSelfIntersecting(path.DebugGridPath).ShouldBeFalse(
                    $"fixture '{fixture.Name}': {connection.Description} visits a grid cell twice");
            }
        }
    }

    /// <summary>Facing pins on one axis — the trivial straight route.</summary>
    private static LayoutTestDefinition StraightFacing() => new()
    {
        Name = "StraightFacing",
        Components = new()
        {
            CreateComponent("Source", 0, 0, ("output", 50, 25, 0)),
            CreateComponent("Detector", 150, 0, ("input", 0, 25, 180)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>Facing pins with a lateral offset — one or two bends.</summary>
    private static LayoutTestDefinition ParallelOffset() => new()
    {
        Name = "ParallelOffset",
        Components = new()
        {
            CreateComponent("Source", 0, 0, ("output", 50, 25, 0)),
            CreateComponent("Detector", 150, 60, ("input", 0, 25, 180)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>Perpendicular offset — the approach needs a 90° turn.</summary>
    private static LayoutTestDefinition Perpendicular() => new()
    {
        Name = "Perpendicular",
        Components = new()
        {
            CreateComponent("Source", 0, 50, ("output", 50, 25, 0)),
            CreateComponent("Detector", 150, 100, ("input", 0, 25, 180)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>Both pins face the same way — the route must make a U-turn.</summary>
    private static LayoutTestDefinition OpposingUTurn() => new()
    {
        Name = "OpposingUTurn",
        Components = new()
        {
            CreateComponent("Source", 0, 0, ("output", 50, 25, 0)),
            CreateComponent("Detector", 150, 0, ("input", 50, 25, 0)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>
    /// Both pins point away from their partner — the Kreisverbindung pattern:
    /// the search must turn around at both ends and must never close a circle.
    /// </summary>
    private static LayoutTestDefinition PinsPointAwayFromEachOther() => new()
    {
        Name = "PinsPointAwayFromEachOther",
        Components = new()
        {
            CreateComponent("Source", 150, 0, ("output", 50, 25, 0)),
            CreateComponent("Detector", 0, 0, ("input", 0, 25, 180)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>Vertical arrangement with facing pins on the horizontal edges.</summary>
    private static LayoutTestDefinition VerticalFacing() => new()
    {
        Name = "VerticalFacing",
        Components = new()
        {
            CreateComponent("Source", 0, 0, ("output", 25, 0, 270)),
            CreateComponent("Detector", 0, 150, ("input", 25, 50, 90)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    /// <summary>Two chained connections sharing a middle component.</summary>
    private static LayoutTestDefinition ChainOfTwoConnections() => new()
    {
        Name = "ChainOfTwoConnections",
        Components = new()
        {
            CreateComponent("A", 0, 0, ("out", 50, 25, 0)),
            CreateComponent("B", 150, 0, ("in", 0, 25, 180), ("out", 50, 25, 0)),
            CreateComponent("C", 300, 0, ("in", 0, 25, 180)),
        },
        Connections = new()
        {
            Connect(0, "out", 1, "in"),
            Connect(1, "out", 2, "in"),
        },
    };

    /// <summary>
    /// Start pin faces away from a target sitting behind and above it; the
    /// target pin faces away too — both ends need a turn-around in tight space.
    /// </summary>
    private static LayoutTestDefinition TargetBehindAndAbove() => new()
    {
        Name = "TargetBehindAndAbove",
        Components = new()
        {
            CreateComponent("Source", 200, 200, ("output", 50, 25, 0)),
            CreateComponent("Detector", 200, 0, ("input", 25, 0, 270)),
        },
        Connections = new() { Connect(0, "output", 1, "input") },
    };

    private static LayoutConnection Connect(int from, string fromPin, int to, string toPin) =>
        new()
        {
            FromComponentIndex = from,
            FromPin = fromPin,
            ToComponentIndex = to,
            ToPin = toPin,
        };

    private static LayoutComponent CreateComponent(
        string type, double x, double y,
        params (string name, double offsetX, double offsetY, double angle)[] pins) =>
        new()
        {
            Type = type,
            X = x,
            Y = y,
            Pins = pins.Select(p => new LayoutPin
            {
                Name = p.name,
                OffsetX = p.offsetX,
                OffsetY = p.offsetY,
                AngleDegrees = p.angle,
            }).ToList(),
        };
}

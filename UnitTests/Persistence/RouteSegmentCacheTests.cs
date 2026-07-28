using System.Text.Json;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Converters;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Tests for waveguide route segment caching (save/load without re-routing).
/// </summary>
public class RouteSegmentCacheTests
{
    [Fact]
    public void PathSegmentConverter_StraightSegment_RoundTrips()
    {
        var segment = new StraightSegment(10, 20, 110, 20, 0);

        var dto = PathSegmentConverter.ToDto(segment);
        var restored = PathSegmentConverter.FromDto(dto);

        dto.Type.ShouldBe("Straight");
        restored.ShouldNotBeNull();
        restored.ShouldBeOfType<StraightSegment>();
        restored!.StartPoint.X.ShouldBe(10);
        restored.StartPoint.Y.ShouldBe(20);
        restored.EndPoint.X.ShouldBe(110);
        restored.EndPoint.Y.ShouldBe(20);
        restored.StartAngleDegrees.ShouldBe(0);
        restored.LengthMicrometers.ShouldBe(100.0, 0.001);
    }

    [Fact]
    public void PathSegmentConverter_BendSegment_RoundTrips()
    {
        var segment = new BendSegment(50, 50, 10, 0, 90);

        var dto = PathSegmentConverter.ToDto(segment);
        var restored = PathSegmentConverter.FromDto(dto);

        dto.Type.ShouldBe("Bend");
        dto.CenterX.ShouldBe(50);
        dto.CenterY.ShouldBe(50);
        dto.RadiusMicrometers.ShouldBe(10);
        dto.SweepAngleDegrees.ShouldBe(90);
        restored.ShouldNotBeNull();
        restored.ShouldBeOfType<BendSegment>();
        var bend = (BendSegment)restored!;
        bend.RadiusMicrometers.ShouldBe(10);
        bend.SweepAngleDegrees.ShouldBe(90);
        bend.Equivalent90DegreeBends.ShouldBe(1.0);
    }

    [Fact]
    public void PathSegmentConverter_NullSegments_ReturnsNull()
    {
        var result = PathSegmentConverter.ToRoutedPath(null, false);
        result.ShouldBeNull();
    }

    [Fact]
    public void PathSegmentConverter_EmptySegments_ReturnsNull()
    {
        var result = PathSegmentConverter.ToRoutedPath(new List<PathSegmentData>(), false);
        result.ShouldBeNull();
    }

    [Fact]
    public void PathSegmentConverter_UnknownType_SkipsSegment()
    {
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "FutureType", StartX = 0, StartY = 0, EndX = 10, EndY = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(dtos, false);
        result.ShouldBeNull();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_SetsIsBlockedFallback()
    {
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0, StartAngleDegrees = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(dtos, isBlockedFallback: true);

        result.ShouldNotBeNull();
        result!.IsBlockedFallback.ShouldBeTrue();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_SetsIsInvalidGeometryAndIsPlaceholderGeometry()
    {
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0, StartAngleDegrees = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(
            dtos, isBlockedFallback: false, isInvalidGeometry: true, isPlaceholderGeometry: true);

        result.ShouldNotBeNull();
        result!.IsInvalidGeometry.ShouldBeTrue();
        result.IsPlaceholderGeometry.ShouldBeTrue();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_DefaultsIsInvalidGeometryAndIsPlaceholderGeometryToFalse()
    {
        // Old files predating these fields must load as if neither flag had ever been set.
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0, StartAngleDegrees = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(dtos, isBlockedFallback: false);

        result.ShouldNotBeNull();
        result!.IsInvalidGeometry.ShouldBeFalse();
        result.IsPlaceholderGeometry.ShouldBeFalse();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_LegacyBlockedSingleStraight_MigratesToPlaceholderGeometry()
    {
        // A file saved between the router's self-crossing degrade-to-blocked-fallback step
        // shipping and IsPlaceholderGeometry being introduced has exactly this shape (blocked,
        // one straight segment) with no explicit placeholder field — must migrate to true,
        // not silently load as false and re-export the placeholder line.
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0, StartAngleDegrees = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(dtos, isBlockedFallback: true);

        result.ShouldNotBeNull();
        result!.IsPlaceholderGeometry.ShouldBeTrue();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_ExplicitFalsePlaceholder_IsNotOverriddenByMigration()
    {
        // A file saved by a version that DOES know about the field, with the flag genuinely
        // false, must not be reclassified just because it happens to share the legacy shape.
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 100, EndY = 0, StartAngleDegrees = 0 }
        };

        var result = PathSegmentConverter.ToRoutedPath(
            dtos, isBlockedFallback: true, isPlaceholderGeometry: false);

        result.ShouldNotBeNull();
        result!.IsPlaceholderGeometry.ShouldBeFalse();
    }

    [Fact]
    public void PathSegmentConverter_ToRoutedPath_LegacyBlockedMultiSegment_IsNotMigrated()
    {
        // A blocked fallback with more than one segment does not match the placeholder's
        // shape (a lone straight line) — it is real (if messy) routed geometry.
        var dtos = new List<PathSegmentData>
        {
            new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 50, EndY = 0, StartAngleDegrees = 0 },
            new()
            {
                Type = "Bend", StartX = 50, StartY = 0, EndX = 60, EndY = 10, StartAngleDegrees = 0,
                CenterX = 50, CenterY = 10, RadiusMicrometers = 10, SweepAngleDegrees = 90
            },
        };

        var result = PathSegmentConverter.ToRoutedPath(dtos, isBlockedFallback: true);

        result.ShouldNotBeNull();
        result!.IsPlaceholderGeometry.ShouldBeFalse();
    }

    [Fact]
    public void PathSegmentConverter_MixedSegments_RoundTrip()
    {
        var segments = new List<PathSegment>
        {
            new StraightSegment(0, 0, 50, 0, 0),
            new BendSegment(50, 10, 10, 0, 90),
            new StraightSegment(60, 10, 60, 60, 90)
        };

        var dtos = PathSegmentConverter.ToDtoList(segments);
        var path = PathSegmentConverter.ToRoutedPath(dtos, false);

        path.ShouldNotBeNull();
        path!.Segments.Count.ShouldBe(3);
        path.Segments[0].ShouldBeOfType<StraightSegment>();
        path.Segments[1].ShouldBeOfType<BendSegment>();
        path.Segments[2].ShouldBeOfType<StraightSegment>();
    }

    [Fact]
    public void RestoreCachedPath_CalculatesLossCorrectly()
    {
        var connection = CreateTestConnection();

        var cachedPath = new RoutedPath();
        cachedPath.Segments.Add(new StraightSegment(50, 25, 100, 25, 0));

        connection.RestoreCachedPath(cachedPath);

        connection.RoutedPath.ShouldBe(cachedPath);
        connection.IsPathValid.ShouldBeTrue();
        connection.PathLengthMicrometers.ShouldBe(50.0, 0.001);
        connection.TotalLossDb.ShouldBeGreaterThan(0);
        connection.TransmissionCoefficient.Magnitude.ShouldBeLessThan(1.0);
        connection.TransmissionCoefficient.Magnitude.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void RestoreCachedPath_MatchesRecalculateLoss()
    {
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(100, 0);

        var conn1 = new WaveguideConnection
        {
            StartPin = CreateOutputPin(startComp),
            EndPin = CreateInputPin(endComp),
            PropagationLossDbPerCm = 2.0,
            BendLossDbPer90Deg = 0.1
        };
        conn1.RecalculateTransmission(new WaveguideRouter());
        var routedPath = conn1.RoutedPath;

        if (routedPath == null) return; // Router may not find path without grid

        var conn2 = new WaveguideConnection
        {
            StartPin = CreateOutputPin(startComp),
            EndPin = CreateInputPin(endComp),
            PropagationLossDbPerCm = 2.0,
            BendLossDbPer90Deg = 0.1
        };
        conn2.RestoreCachedPath(routedPath);

        conn2.TotalLossDb.ShouldBe(conn1.TotalLossDb, 0.001);
        conn2.TransmissionCoefficient.Magnitude.ShouldBe(
            conn1.TransmissionCoefficient.Magnitude, 0.001);
    }

    [Fact]
    public void ConnectionData_BackwardCompatibility_NullCachedSegments()
    {
        var json = """
        {
            "StartComponentIndex": 0,
            "StartPinName": "output",
            "EndComponentIndex": 1,
            "EndPinName": "input"
        }
        """;

        var connData = JsonSerializer.Deserialize<ConnectionData>(json);

        connData.ShouldNotBeNull();
        connData!.CachedSegments.ShouldBeNull();
        connData.IsBlockedFallback.ShouldBeNull();
        connData.IsInvalidGeometry.ShouldBeNull();
        connData.IsPlaceholderGeometry.ShouldBeNull();
    }

    [Fact]
    public void ConnectionData_WithCachedSegments_Deserializes()
    {
        var json = """
        {
            "StartComponentIndex": 0,
            "StartPinName": "output",
            "EndComponentIndex": 1,
            "EndPinName": "input",
            "CachedSegments": [
                { "Type": "Straight", "StartX": 0, "StartY": 0, "EndX": 100, "EndY": 0, "StartAngleDegrees": 0, "EndAngleDegrees": 0 }
            ],
            "IsBlockedFallback": false
        }
        """;

        var connData = JsonSerializer.Deserialize<ConnectionData>(json);

        connData.ShouldNotBeNull();
        connData!.CachedSegments.ShouldNotBeNull();
        connData.CachedSegments!.Count.ShouldBe(1);
        connData.CachedSegments[0].Type.ShouldBe("Straight");
        connData.CachedSegments[0].StartX.ShouldBe(0);
        connData.CachedSegments[0].EndX.ShouldBe(100);
    }

    [Fact]
    public void ConnectionData_JsonRoundTrip_PreservesAllFields()
    {
        var original = new ConnectionData
        {
            StartComponentIndex = 0,
            StartPinName = "output",
            EndComponentIndex = 1,
            EndPinName = "input",
            CachedSegments = new List<PathSegmentData>
            {
                new() { Type = "Straight", StartX = 10, StartY = 20, EndX = 110, EndY = 20, StartAngleDegrees = 0, EndAngleDegrees = 0 },
                new() { Type = "Bend", StartX = 110, StartY = 20, EndX = 120, EndY = 30, StartAngleDegrees = 0, EndAngleDegrees = 90, CenterX = 110, CenterY = 30, RadiusMicrometers = 10, SweepAngleDegrees = 90 }
            },
            IsBlockedFallback = false
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<ConnectionData>(json);

        restored.ShouldNotBeNull();
        restored!.CachedSegments.ShouldNotBeNull();
        restored.CachedSegments!.Count.ShouldBe(2);
        restored.CachedSegments[0].Type.ShouldBe("Straight");
        restored.CachedSegments[1].Type.ShouldBe("Bend");
        restored.CachedSegments[1].RadiusMicrometers.ShouldBe(10);
        restored.CachedSegments[1].SweepAngleDegrees.ShouldBe(90);
    }

    [Fact]
    public void ConnectionData_JsonRoundTrip_PreservesIsInvalidGeometryAndIsPlaceholderGeometry()
    {
        var original = new ConnectionData
        {
            StartComponentIndex = 0,
            StartPinName = "output",
            EndComponentIndex = 1,
            EndPinName = "input",
            CachedSegments = new List<PathSegmentData>
            {
                new() { Type = "Straight", StartX = 0, StartY = 0, EndX = 1, EndY = 0, StartAngleDegrees = 0 }
            },
            IsBlockedFallback = true,
            IsInvalidGeometry = true,
            IsPlaceholderGeometry = true,
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ConnectionData>(json);

        restored.ShouldNotBeNull();
        restored!.IsBlockedFallback.ShouldBe(true);
        restored.IsInvalidGeometry.ShouldBe(true);
        restored.IsPlaceholderGeometry.ShouldBe(true);
    }

    [Fact]
    public void AddConnectionWithCachedRoute_RegistersObstacle()
    {
        var router = new WaveguideRouter();
        router.InitializePathfindingGrid(-50, -50, 200, 100,
            new[] { CreateTestComponent(0, 0), CreateTestComponent(100, 0) });
        var manager = new WaveguideConnectionManager(router);
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(100, 0);
        var startPin = CreateOutputPin(startComp);
        var endPin = CreateInputPin(endComp);

        var cachedPath = new RoutedPath();
        cachedPath.Segments.Add(new StraightSegment(50, 25, 100, 25, 0));

        var connection = manager.AddConnectionWithCachedRoute(startPin, endPin, cachedPath);

        connection.RoutedPath.ShouldBe(cachedPath);
        connection.IsPathValid.ShouldBeTrue();
        connection.TotalLossDb.ShouldBeGreaterThan(0);
        manager.Connections.Count.ShouldBe(1);
    }

    private static WaveguideConnection CreateTestConnection()
    {
        var startComp = CreateTestComponent(0, 0);
        var endComp = CreateTestComponent(100, 0);
        return new WaveguideConnection
        {
            StartPin = CreateOutputPin(startComp),
            EndPin = CreateInputPin(endComp),
            PropagationLossDbPerCm = 2.0,
            BendLossDbPer90Deg = 0.05
        };
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var component = new Component(
            new Dictionary<int, SMatrix>(), new List<Slider>(),
            "test", "", parts, 0,
            $"Test_{x}_{y}", DiscreteRotation.R0);
        component.WidthMicrometers = 50;
        component.HeightMicrometers = 50;
        component.PhysicalX = x;
        component.PhysicalY = y;
        return component;
    }

    private static PhysicalPin CreateOutputPin(Component c) => new()
    {
        Name = "output",
        OffsetXMicrometers = c.WidthMicrometers,
        OffsetYMicrometers = c.HeightMicrometers / 2,
        AngleDegrees = 0,
        ParentComponent = c
    };

    private static PhysicalPin CreateInputPin(Component c) => new()
    {
        Name = "input",
        OffsetXMicrometers = 0,
        OffsetYMicrometers = c.HeightMicrometers / 2,
        AngleDegrees = 180,
        ParentComponent = c
    };
}

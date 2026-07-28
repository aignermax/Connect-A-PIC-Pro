using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Unit tests for <see cref="ExportableConnections"/> — the single, shared definition of
/// which routed connections may render as GDS geometry. A connection with no computed
/// route, a blocked fallback (drawn through an obstacle), or invalid geometry (e.g. a
/// bend radius violation) must never be considered exportable, regardless of which
/// backend (Nazca or gdsfactory) asks.
/// </summary>
public class ExportableConnectionsTests
{
    [Fact]
    public void IsExportable_NoRoutedPath_ReturnsFalse()
    {
        var connection = CreateConnection();

        connection.IsExportable().ShouldBeFalse();
    }

    [Fact]
    public void IsExportable_BlockedFallback_ReturnsFalse()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsBlockedFallback = true;
        connection.RestoreCachedPath(path);

        connection.IsExportable().ShouldBeFalse();
    }

    [Fact]
    public void IsExportable_InvalidGeometry_ReturnsFalse()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);

        connection.IsExportable().ShouldBeFalse();
    }

    [Fact]
    public void IsExportable_ValidRoutedPath_ReturnsTrue()
    {
        var connection = CreateConnection();
        connection.RestoreCachedPath(CreateStraightPath());

        connection.IsExportable().ShouldBeTrue();
    }

    [Fact]
    public void WhereExportable_MixOfValidAndBroken_KeepsOnlyValid()
    {
        var valid = CreateConnection();
        valid.RestoreCachedPath(CreateStraightPath());

        var blocked = CreateConnection();
        var blockedPath = CreateStraightPath();
        blockedPath.IsBlockedFallback = true;
        blocked.RestoreCachedPath(blockedPath);

        var routeless = CreateConnection();

        var result = new[] { valid, blocked, routeless }.WhereExportable().ToList();

        result.ShouldBe(new[] { valid });
    }

    [Fact]
    public void CollectSkipped_MixOfValidAndBroken_ReturnsOnlyBroken()
    {
        var valid = CreateConnection();
        valid.RestoreCachedPath(CreateStraightPath());

        var invalid = CreateConnection();
        var invalidPath = CreateStraightPath();
        invalidPath.IsInvalidGeometry = true;
        invalid.RestoreCachedPath(invalidPath);

        var routeless = CreateConnection();

        var result = new[] { valid, invalid, routeless }.CollectSkipped();

        result.Count.ShouldBe(2);
        result.ShouldContain(invalid);
        result.ShouldContain(routeless);
    }

    [Fact]
    public void CollectSkipped_AllValid_ReturnsEmpty()
    {
        var a = CreateConnection();
        a.RestoreCachedPath(CreateStraightPath());
        var b = CreateConnection();
        b.RestoreCachedPath(CreateStraightPath());

        var result = new[] { a, b }.CollectSkipped();

        result.ShouldBeEmpty();
    }

    private static WaveguideConnection CreateConnection()
    {
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalPins.Add(new PhysicalPin { Name = "pin_a", ParentComponent = comp1 });
        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp2.PhysicalX = 100;
        comp2.PhysicalPins.Add(new PhysicalPin { Name = "pin_b", ParentComponent = comp2 });

        return new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins.Last(),
            EndPin = comp2.PhysicalPins.Last(),
        };
    }

    private static RoutedPath CreateStraightPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        return path;
    }
}

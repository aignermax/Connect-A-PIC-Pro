using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Unit tests for <see cref="ExportableConnections"/> — the single, shared definition of
/// which routed connections may render as GDS geometry. Only a placeholder route (the
/// router's honest stand-in for a self-crossing fallback with no optical model) or invalid
/// geometry (e.g. a bend radius violation) is excluded. A missing route is NOT excluded — it
/// is the long-standing routeless state both exporters draw as a direct pin-to-pin fallback.
/// <see cref="RoutedPath.IsBlockedFallback"/> alone does NOT exclude either: besides the
/// placeholder case it also covers a fallback that merely grazes an obstacle (real,
/// exportable geometry) and the crossing diagnostic stamped on an unresolved sibling overlap
/// — including one a bridge marker legitimately resolves.
/// </summary>
public class ExportableConnectionsTests
{
    [Fact]
    public void IsExportable_NoRoutedPath_ReturnsTrue()
    {
        var connection = CreateConnection();

        connection.IsExportable().ShouldBeTrue();
    }

    [Fact]
    public void IsExportable_BlockedFallbackAlone_ReturnsTrue()
    {
        // A fallback that merely grazes an obstacle, or the crossing diagnostic on an
        // unresolved sibling overlap (e.g. a bridge-resolved metal/optical crossing) — both
        // are real, exportable geometry and must not be excluded.
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsBlockedFallback = true;
        connection.RestoreCachedPath(path);

        connection.IsExportable().ShouldBeTrue();
    }

    [Fact]
    public void IsExportable_PlaceholderGeometry_ReturnsFalse()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsPlaceholderGeometry = true;
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
    public void TryRecordSkip_ExportableRoute_ReturnsFalse_RecordsNothing()
    {
        var connection = CreateConnection();
        connection.RestoreCachedPath(CreateStraightPath());
        var skipped = new List<string>();

        var wasSkipped = ExportableConnections.TryRecordSkip(
            connection.RoutedPath, connection.StartPin, connection.EndPin, skipped);

        wasSkipped.ShouldBeFalse();
        skipped.ShouldBeEmpty();
    }

    [Fact]
    public void TryRecordSkip_PlaceholderRoute_ReturnsTrue_RecordsDescription()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsPlaceholderGeometry = true;
        connection.RestoreCachedPath(path);
        var skipped = new List<string>();

        var wasSkipped = ExportableConnections.TryRecordSkip(
            connection.RoutedPath, connection.StartPin, connection.EndPin, skipped);

        wasSkipped.ShouldBeTrue();
        skipped.ShouldHaveSingleItem();
        skipped[0].ShouldBe(ExportableConnections.Describe(connection.StartPin, connection.EndPin));
    }

    [Fact]
    public void TryRecordSkip_InvalidGeometry_ReturnsTrue_RecordsDescription()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);
        var skipped = new List<string>();

        var wasSkipped = ExportableConnections.TryRecordSkip(
            connection.RoutedPath, connection.StartPin, connection.EndPin, skipped);

        wasSkipped.ShouldBeTrue();
        skipped.ShouldHaveSingleItem();
    }

    [Fact]
    public void TryRecordSkip_NoCollector_StillReportsSkipViaReturnValue_NoException()
    {
        var connection = CreateConnection();
        var path = CreateStraightPath();
        path.IsPlaceholderGeometry = true;
        connection.RestoreCachedPath(path);

        Should.NotThrow(() => ExportableConnections.TryRecordSkip(
            connection.RoutedPath, connection.StartPin, connection.EndPin, null)).ShouldBeTrue();
    }

    [Fact]
    public void Describe_BothPinsPresent_FormatsStartArrowEnd()
    {
        var connection = CreateConnection();

        var expected = $"{connection.StartPin.ParentComponent!.Identifier}.{connection.StartPin.Name} → " +
                       $"{connection.EndPin.ParentComponent!.Identifier}.{connection.EndPin.Name}";
        ExportableConnections.Describe(connection.StartPin, connection.EndPin).ShouldBe(expected);
    }

    [Fact]
    public void Describe_NullPin_FallsBackToQuestionMark_NoException()
    {
        Should.NotThrow(() => ExportableConnections.Describe(null, null)).ShouldBe("? → ?");
    }

    [Fact]
    public void Describe_PinWithoutParentComponent_FallsBackToQuestionMarkForThatEndpoint()
    {
        var orphanPin = new PhysicalPin { Name = "dangling", ParentComponent = null! };

        Should.NotThrow(() => ExportableConnections.Describe(orphanPin, null)).ShouldBe("?.dangling → ?");
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

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
    public void WhereExportable_MixOfValidRoutelessAndPlaceholder_KeepsEverythingButThePlaceholder()
    {
        var valid = CreateConnection();
        valid.RestoreCachedPath(CreateStraightPath());

        var placeholder = CreateConnection();
        var placeholderPath = CreateStraightPath();
        placeholderPath.IsPlaceholderGeometry = true;
        placeholder.RestoreCachedPath(placeholderPath);

        // A missing route falls back to the pin-to-pin straight — it stays exportable.
        var routeless = CreateConnection();

        var result = new[] { valid, placeholder, routeless }.WhereExportable().ToList();

        result.ShouldBe(new[] { valid, routeless });
    }

    [Fact]
    public void CollectSkipped_MixOfValidAndBroken_ReturnsOnlyPlaceholderAndInvalid()
    {
        var valid = CreateConnection();
        valid.RestoreCachedPath(CreateStraightPath());

        var invalid = CreateConnection();
        var invalidPath = CreateStraightPath();
        invalidPath.IsInvalidGeometry = true;
        invalid.RestoreCachedPath(invalidPath);

        var placeholder = CreateConnection();
        var placeholderPath = CreateStraightPath();
        placeholderPath.IsPlaceholderGeometry = true;
        placeholder.RestoreCachedPath(placeholderPath);

        // Routeless and blocked-fallback-only connections are NOT skipped.
        var routeless = CreateConnection();
        var blocked = CreateConnection();
        var blockedPath = CreateStraightPath();
        blockedPath.IsBlockedFallback = true;
        blocked.RestoreCachedPath(blockedPath);

        var result = new[] { valid, invalid, placeholder, routeless, blocked }.CollectSkipped();

        result.Count.ShouldBe(2);
        result.ShouldContain(invalid);
        result.ShouldContain(placeholder);
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

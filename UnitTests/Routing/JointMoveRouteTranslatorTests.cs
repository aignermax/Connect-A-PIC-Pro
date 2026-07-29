using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing;

/// <summary>
/// Unit tests for <see cref="JointMoveRouteTranslator"/> (issue #805): moving BOTH
/// connected components by the same delta must translate the existing route instead of
/// invalidating it; any other change (single move, rotation) must leave it untouched so
/// the normal re-route path runs.
/// </summary>
public class JointMoveRouteTranslatorTests
{
    private const double JointMoveDeltaX = 120.0;
    private const double JointMoveDeltaY = -35.0;
    private const double CoordinateTolerance = 1e-9;

    [Fact]
    public void TryTranslateToPins_BothComponentsMovedEqually_TranslatesPathExactly()
    {
        var (conn, startComponent, endComponent) = CreateRoutedConnection();
        var originalPoints = SegmentPointsOf(conn);

        startComponent.PhysicalX += JointMoveDeltaX;
        startComponent.PhysicalY += JointMoveDeltaY;
        endComponent.PhysicalX += JointMoveDeltaX;
        endComponent.PhysicalY += JointMoveDeltaY;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeTrue();

        conn.FrozenPathStillMatchesPins().ShouldBeTrue("translated path must match the moved pins");
        var translatedPoints = SegmentPointsOf(conn);
        translatedPoints.Count.ShouldBe(originalPoints.Count);
        for (int i = 0; i < originalPoints.Count; i++)
        {
            translatedPoints[i].X.ShouldBe(originalPoints[i].X + JointMoveDeltaX, CoordinateTolerance);
            translatedPoints[i].Y.ShouldBe(originalPoints[i].Y + JointMoveDeltaY, CoordinateTolerance);
        }
    }

    [Fact]
    public void TryTranslateToPins_TranslationPreservesBendGeometry()
    {
        var (conn, startComponent, endComponent) = CreateRoutedConnection(offsetEndY: 200);
        var originalBends = conn.RoutedPath!.Segments.OfType<BendSegment>().ToList();
        originalBends.ShouldNotBeEmpty("the offset layout must contain bends");
        var originalRadii = originalBends.Select(b => b.RadiusMicrometers).ToList();
        var originalSweeps = originalBends.Select(b => b.SweepAngleDegrees).ToList();

        startComponent.PhysicalX += JointMoveDeltaX;
        endComponent.PhysicalX += JointMoveDeltaX;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeTrue();

        var translatedBends = conn.RoutedPath!.Segments.OfType<BendSegment>().ToList();
        translatedBends.Select(b => b.RadiusMicrometers).ShouldBe(originalRadii);
        translatedBends.Select(b => b.SweepAngleDegrees).ShouldBe(originalSweeps);
        for (int i = 0; i < originalBends.Count; i++)
        {
            translatedBends[i].Center.X.ShouldBe(originalBends[i].Center.X + JointMoveDeltaX, CoordinateTolerance);
            translatedBends[i].Center.Y.ShouldBe(originalBends[i].Center.Y, CoordinateTolerance);
        }
    }

    [Fact]
    public void TryTranslateToPins_OnlyOneComponentMoved_ReturnsFalseAndKeepsPath()
    {
        var (conn, _, endComponent) = CreateRoutedConnection();
        var originalPath = conn.RoutedPath;

        endComponent.PhysicalX += JointMoveDeltaX;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeFalse();
        conn.RoutedPath.ShouldBeSameAs(originalPath, "an unequal-delta move must not touch the path");
    }

    [Fact]
    public void TryTranslateToPins_NothingMoved_ReturnsFalse()
    {
        var (conn, _, _) = CreateRoutedConnection();
        var originalPath = conn.RoutedPath;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeFalse();
        conn.RoutedPath.ShouldBeSameAs(originalPath);
    }

    [Fact]
    public void TryTranslateToPins_EqualDeltasButPinDirectionChanged_ReturnsFalse()
    {
        var (conn, startComponent, endComponent) = CreateRoutedConnection();

        // Simulate a rotation that coincidentally moves both pins by the same delta:
        // the pins' angles flip while the absolute positions shift uniformly.
        startComponent.PhysicalX += JointMoveDeltaX;
        endComponent.PhysicalX += JointMoveDeltaX;
        conn.StartPin.AngleDegrees += 90;
        conn.EndPin.AngleDegrees += 90;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeFalse(
            "a direction change is a rotation, not a translation");
    }

    [Fact]
    public void TryTranslateToPins_BlockedFallbackPath_ReturnsFalse()
    {
        var (conn, startComponent, endComponent) = CreateRoutedConnection();
        conn.RoutedPath!.IsBlockedFallback = true;

        startComponent.PhysicalX += JointMoveDeltaX;
        endComponent.PhysicalX += JointMoveDeltaX;

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeFalse(
            "emergency geometry must be re-routed, never carried along");
    }

    [Fact]
    public void TryTranslateToPins_NoPath_ReturnsFalse()
    {
        var conn = new WaveguideConnection();

        JointMoveRouteTranslator.TryTranslateToPins(conn).ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<(double X, double Y)> SegmentPointsOf(WaveguideConnection conn) =>
        conn.RoutedPath!.Segments
            .SelectMany(s => new[] { s.StartPoint, s.EndPoint })
            .ToList();

    private static (WaveguideConnection Connection, Component StartComponent, Component EndComponent)
        CreateRoutedConnection(double offsetEndY = 0)
    {
        var startComponent = CreateTestComponent(0, 0);
        var endComponent = CreateTestComponent(400, offsetEndY);

        var conn = new WaveguideConnection
        {
            StartPin = new PhysicalPin
            {
                Name = "output",
                OffsetXMicrometers = 50,
                OffsetYMicrometers = 25,
                AngleDegrees = 0,
                ParentComponent = startComponent,
            },
            EndPin = new PhysicalPin
            {
                Name = "input",
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 25,
                AngleDegrees = 180,
                ParentComponent = endComponent,
            },
        };

        conn.RecalculateTransmission(new WaveguideRouter());
        conn.RoutedPath.ShouldNotBeNull();
        return (conn, startComponent, endComponent);
    }

    private static Component CreateTestComponent(double x, double y)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: $"TestComponent_{x}_{y}",
            rotationCounterClock: DiscreteRotation.R0)
        {
            WidthMicrometers = 50,
            HeightMicrometers = 50,
            PhysicalX = x,
            PhysicalY = y,
        };
    }
}

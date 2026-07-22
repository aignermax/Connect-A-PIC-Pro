using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

public class BendRadiusEditorTests
{
    private const double Tolerance = 1e-6;

    /// <summary>
    /// Builds a connection with a routed path: straight (0,0)→(50,0), 90° bend
    /// of the given radius, straight up to (60+r-10 offsets handled by geometry).
    /// </summary>
    private static WaveguideConnection CreateConnectionWithBend(double radius = 10)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        // BendSegment(center=(50, radius), r, startAngle=0, sweep=+90):
        // StartPoint=(50,0), EndPoint=(50+r, radius)
        path.Segments.Add(new BendSegment(50, radius, radius, 0, 90));
        path.Segments.Add(new StraightSegment(50 + radius, radius, 50 + radius, 60, 90));

        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(path);
        return conn;
    }

    [Fact]
    public void TryApplyOverride_ValidRadius_RebuildsBendAndAdjacentStraights()
    {
        var conn = CreateConnectionWithBend(radius: 10);
        var segments = conn.RoutedPath!.Segments;

        var ok = BendRadiusEditor.TryApplyOverride(conn, 0, 20, out var error);

        ok.ShouldBeTrue(error);
        error.ShouldBeNull();

        var bend = (BendSegment)segments[1];
        bend.RadiusMicrometers.ShouldBe(20, Tolerance);
        // Corner is at (60, 0); new tangent length = 20·tan(45°) = 20.
        bend.StartPoint.X.ShouldBe(40, Tolerance);
        bend.StartPoint.Y.ShouldBe(0, Tolerance);
        bend.EndPoint.X.ShouldBe(60, Tolerance);
        bend.EndPoint.Y.ShouldBe(20, Tolerance);

        ((StraightSegment)segments[0]).EndPoint.ShouldBe(bend.StartPoint);
        ((StraightSegment)segments[2]).StartPoint.ShouldBe(bend.EndPoint);
        conn.RoutedPath.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void TryApplyOverride_Success_FreezesRouteAndRecordsOverride()
    {
        var conn = CreateConnectionWithBend();

        BendRadiusEditor.TryApplyOverride(conn, 0, 15, out _).ShouldBeTrue();

        conn.IsRouteFrozen.ShouldBeTrue();
        conn.BendRadiusOverrides.ShouldContainKeyAndValue(0, 15);
    }

    [Fact]
    public void TryApplyOverride_Success_RefreshesPathLengthAndLoss()
    {
        var conn = CreateConnectionWithBend(radius: 10);
        var lossBefore = conn.TotalLossDb;

        BendRadiusEditor.TryApplyOverride(conn, 0, 30, out _).ShouldBeTrue();

        // A larger radius shortens the straights but lengthens the arc; loss must be refreshed.
        conn.TotalLossDb.ShouldNotBe(lossBefore);
    }

    [Fact]
    public void TryApplyOverride_RadiusTooLarge_FailsWithError()
    {
        var conn = CreateConnectionWithBend(radius: 10);

        // Tangent length 100 exceeds both adjacent straights (60 µm to the corner).
        var ok = BendRadiusEditor.TryApplyOverride(conn, 0, 100, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        conn.IsRouteFrozen.ShouldBeFalse();
        conn.BendRadiusOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void TryApplyOverride_BendIndexOutOfRange_Fails()
    {
        var conn = CreateConnectionWithBend();

        BendRadiusEditor.TryApplyOverride(conn, 3, 20, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryApplyOverride_NoRoutedPath_Fails()
    {
        var conn = new WaveguideConnection();

        BendRadiusEditor.TryApplyOverride(conn, 0, 20, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryApplyOverride_BendDirectlyAtPin_Fails()
    {
        var path = new RoutedPath();
        path.Segments.Add(new BendSegment(0, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(10, 10, 10, 60, 90));
        var conn = new WaveguideConnection();
        conn.RestoreCachedPath(path);

        BendRadiusEditor.TryApplyOverride(conn, 0, 20, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryApplyOverride_BelowProcessMinimum_FailsWithError()
    {
        var conn = CreateConnectionWithBend(radius: 10);

        var ok = BendRadiusEditor.TryApplyOverride(conn, 0, 3, out var error, minRadiusMicrometers: 5);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("5");
        conn.IsRouteFrozen.ShouldBeFalse();
        conn.BendRadiusOverrides.ShouldBeEmpty();
    }

    [Fact]
    public void TryApplyOverride_AtProcessMinimum_Succeeds()
    {
        var conn = CreateConnectionWithBend(radius: 10);

        BendRadiusEditor.TryApplyOverride(conn, 0, 5, out var error, minRadiusMicrometers: 5)
            .ShouldBeTrue(error);
        conn.BendRadiusOverrides.ShouldContainKeyAndValue(0, 5);
    }

    [Fact]
    public void TryApplyOverride_DefaultMinimum_PreservesAbsoluteFloor()
    {
        var conn = CreateConnectionWithBend(radius: 10);

        // No explicit minimum → default absolute floor of 0.1 µm still applies.
        BendRadiusEditor.TryApplyOverride(conn, 0, 0.05, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();

        // A radius above the absolute floor but below a typical process minimum still succeeds
        // when no process minimum is supplied (preserves prior behaviour / existing tests).
        BendRadiusEditor.TryApplyOverride(conn, 0, 1, out var ok).ShouldBeTrue();
        ok.ShouldBeNull();
    }

    [Fact]
    public void CountBends_CountsOnlyBendSegments()
    {
        var conn = CreateConnectionWithBend();

        BendRadiusEditor.CountBends(conn.RoutedPath!.Segments).ShouldBe(1);
    }
}

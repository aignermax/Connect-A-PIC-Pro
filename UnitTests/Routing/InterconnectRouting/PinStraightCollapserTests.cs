using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting.SegmentShift;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Unit behaviour of the automatic pin-lead collapse (<see cref="PinStraightCollapser"/>): it
/// drives the pin-side straights to zero by reusing the honest segment-shift clamp, and it fully
/// reverts any shift the acceptance predicate rejects (collision / self-intersection surrogate),
/// so the collapsed path is never worse than the smoothed input.
/// </summary>
public class PinStraightCollapserTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void Collapse_ZPath_WithParallelPinLeads_IsLeftUnchanged()
    {
        var path = ZPath();
        var before = Snapshot(path);

        PinStraightCollapser.Collapse(path, _ => true);

        // Both Z-path leads point east and share one middle straight: shortening one lengthens
        // the other by the same amount, so there is no net win. The collapse must leave it be
        // rather than pull one bend off its pin to hug the other.
        var after = Snapshot(path);
        for (int i = 0; i < before.Count; i++)
        {
            after[i].sx.ShouldBe(before[i].sx, Tolerance);
            after[i].sy.ShouldBe(before[i].sy, Tolerance);
            after[i].ex.ShouldBe(before[i].ex, Tolerance);
            after[i].ey.ShouldBe(before[i].ey, Tolerance);
        }
    }

    [Fact]
    public void Collapse_UTurn_WithAntiparallelPinLeads_CollapsesBothLeadsToZero()
    {
        // Both leads point along +x but the arrival lead runs back the other way (heading 180°),
        // so a single shift of the shared middle straight drives both to zero. The zero-length
        // leads are kept (matching the manual segment-shift), and their bends now start at the pins.
        var path = UTurnPath();

        PinStraightCollapser.Collapse(path, _ => true);

        path.IsValid.ShouldBeTrue();
        ((StraightSegment)path.Segments[0]).LengthMicrometers.ShouldBe(0, Tolerance);
        ((StraightSegment)path.Segments[^1]).LengthMicrometers.ShouldBe(0, Tolerance);
        path.Segments[1].ShouldBeOfType<BendSegment>().StartPoint.X.ShouldBe(0, Tolerance);
        path.Segments[1].StartPoint.Y.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void Collapse_WhenOnlyPartOfTheShiftIsAccepted_KeepsTheLargestAcceptedPartialCollapse()
    {
        // Acceptance emulates an obstacle boundary: the start lead may not drop below 10 µm.
        // The full collapse is rejected, so the bisection must settle on the largest accepted
        // partial shift instead of giving up entirely.
        var path = UTurnPath();

        PinStraightCollapser.Collapse(path,
            trial => ((StraightSegment)trial.Segments[0]).LengthMicrometers >= 10.0 - 1e-9);

        path.IsValid.ShouldBeTrue();
        ((StraightSegment)path.Segments[0]).LengthMicrometers.ShouldBe(10.0, 0.1);
        ((StraightSegment)path.Segments[^1]).LengthMicrometers.ShouldBe(10.0, 0.1);
    }

    [Fact]
    public void Collapse_WhenAcceptanceRejects_LeavesThePathUntouched()
    {
        var path = ZPath();
        var before = Snapshot(path);

        PinStraightCollapser.Collapse(path, _ => false);

        var after = Snapshot(path);
        after.Count.ShouldBe(before.Count);
        for (int i = 0; i < before.Count; i++)
        {
            after[i].sx.ShouldBe(before[i].sx, Tolerance);
            after[i].sy.ShouldBe(before[i].sy, Tolerance);
            after[i].ex.ShouldBe(before[i].ex, Tolerance);
            after[i].ey.ShouldBe(before[i].ey, Tolerance);
        }
    }

    [Fact]
    public void Collapse_ShortPath_WithoutShiftableStraight_IsANoOp()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 60, 90));
        var before = Snapshot(path);

        PinStraightCollapser.Collapse(path, _ => true);

        var after = Snapshot(path);
        for (int i = 0; i < before.Count; i++)
        {
            after[i].sx.ShouldBe(before[i].sx, Tolerance);
            after[i].ex.ShouldBe(before[i].ex, Tolerance);
        }
    }

    [Fact]
    public void Collapse_LongPath_CollapsesBothIndependentPinLeads()
    {
        var path = SevenSegmentPath();

        PinStraightCollapser.Collapse(path, _ => true);

        path.IsValid.ShouldBeTrue("collapsing must keep the segments connected");
        ((StraightSegment)path.Segments[0]).LengthMicrometers.ShouldBe(0, Tolerance);
        ((StraightSegment)path.Segments[^1]).LengthMicrometers.ShouldBe(0, Tolerance);
    }

    /// <summary>Z-shaped path with two 50 µm pin leads and one shiftable middle straight — the
    /// same fixture the segment-shift geometry tests use.</summary>
    private static RoutedPath ZPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(60, 10, 60, 50, 90));
        path.Segments.Add(new BendSegment(70, 50, 10, 90, -90));
        path.Segments.Add(new StraightSegment(70, 60, 120, 60, 0));
        return path;
    }

    /// <summary>U-turn: 20 µm east pin lead, up, north middle straight, up again to heading west,
    /// 20 µm west pin lead. The two leads are antiparallel, so one shift collapses both.</summary>
    private static RoutedPath UTurnPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 20, 0, 0));
        path.Segments.Add(new BendSegment(20, 10, 10, 0, 90));
        path.Segments.Add(new StraightSegment(30, 10, 30, 50, 90));
        path.Segments.Add(new BendSegment(20, 50, 10, 90, 90));
        path.Segments.Add(new StraightSegment(20, 60, 0, 60, 180));
        return path;
    }

    /// <summary>Staircase with independent shiftable straights next to each pin lead:
    /// east lead → up → east → up → east lead. Each pin lead has its own middle straight.</summary>
    private static RoutedPath SevenSegmentPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 50, 0, 0));      // start pin lead
        path.Segments.Add(new BendSegment(50, 10, 10, 0, 90));       // up
        path.Segments.Add(new StraightSegment(60, 10, 60, 60, 90));  // shiftable A
        path.Segments.Add(new BendSegment(70, 60, 10, 90, -90));     // right
        path.Segments.Add(new StraightSegment(70, 70, 120, 70, 0));  // shiftable B
        path.Segments.Add(new BendSegment(120, 80, 10, 0, 90));      // up
        path.Segments.Add(new StraightSegment(130, 80, 130, 130, 90)); // end pin lead
        return path;
    }

    private static List<(double sx, double sy, double ex, double ey)> Snapshot(RoutedPath path)
        => path.Segments
            .Select(s => (s.StartPoint.X, s.StartPoint.Y, s.EndPoint.X, s.EndPoint.Y))
            .ToList();
}

using CAP.Avalonia.Services.GdsImport;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsFreePinPairer"/>: nearest-opposing pairing, the
/// 180° ± 10° opposition cone, radius cutoff, ambiguity skips, same-instance
/// exclusion, and one-partner-per-pin. Pure geometry — no canvas involved.
/// </summary>
public class GdsFreePinPairerTests
{
    private static GdsFreePinCandidate Pin(
        string label, double x, double y, double angleDegrees, int owner = 0) =>
        new(label, x, y, angleDegrees, owner);

    [Fact]
    public void Pair_EmptyCandidates_ReturnsNothing()
    {
        var pairing = GdsFreePinPairer.Pair(Array.Empty<GdsFreePinCandidate>(), radiusUm: 100);

        pairing.Pairs.ShouldBeEmpty();
        pairing.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Pair_TwoOpposingPins_PairsThemWithDistance()
    {
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", 90, 0, 180, owner: 1) },
            radiusUm: 1000);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        (pair.A, pair.B).ShouldBe((0, 1));
        pair.DistanceUm.ShouldBe(90, 1e-9);
        pairing.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Pair_NearestOpposingWins()
    {
        // a.out sees b.in (100 µm) and c.in (150 µm), both opposing — the nearer one wins.
        var pairing = GdsFreePinPairer.Pair(
            new[]
            {
                Pin("a.out", 0, 0, 0, owner: 0),
                Pin("b.in", 100, 0, 180, owner: 1),
                Pin("c.in", 150, 0, 180, owner: 2),
            },
            radiusUm: 1000);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        (pair.A, pair.B).ShouldBe((0, 1));
        pairing.Skipped.ShouldHaveSingleItem().Reason.ShouldBe(
            GdsFreePinSkipReason.NoOpposingPartnerInRadius, "c.in is left over once a.out and b.in are paired");
    }

    [Theory]
    [InlineData(180.0, true)]
    [InlineData(170.0, true)]   // lower inclusive edge of the 180° ± 10° cone
    [InlineData(190.0, true)]   // upper inclusive edge (wraps to 170°)
    [InlineData(169.9, false)]
    [InlineData(190.1, false)]
    [InlineData(90.0, false)]
    [InlineData(0.0, false)]    // parallel, not opposing
    public void Pair_OpposingAngleTolerance(double partnerAngle, bool shouldPair)
    {
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", 100, 0, partnerAngle, owner: 1) },
            radiusUm: 1000);

        pairing.Pairs.Count.ShouldBe(shouldPair ? 1 : 0);
        if (!shouldPair)
        {
            pairing.Skipped.Count.ShouldBe(2, "both pins stay free when the angle does not oppose");
            pairing.Skipped.ShouldAllBe(s => s.Reason == GdsFreePinSkipReason.NoOpposingPartnerInRadius);
        }
    }

    [Theory]
    [InlineData(100.0, true)]   // radius boundary is inclusive
    [InlineData(100.1, false)]
    public void Pair_RadiusCutoff(double distance, bool shouldPair)
    {
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", distance, 0, 180, owner: 1) },
            radiusUm: 100);

        pairing.Pairs.Count.ShouldBe(shouldPair ? 1 : 0);
    }

    [Fact]
    public void Pair_AmbiguousNearestCandidates_SkipsPinWithReasonAndDistances()
    {
        // a.out's two opposing candidates are 0.5 µm apart (< AmbiguityDeltaUm) — no guess.
        var pairing = GdsFreePinPairer.Pair(
            new[]
            {
                Pin("a.out", 0, 0, 0, owner: 0),
                Pin("b.in", 100.0, 0, 180, owner: 1),
                Pin("c.in", 100.5, 0, 180, owner: 2),
            },
            radiusUm: 1000);

        pairing.Pairs.ShouldBeEmpty();
        pairing.Skipped.Count.ShouldBe(3, "the ambiguous pin is unavailable, leaving b.in/c.in without partners");
        var ambiguous = pairing.Skipped[0];
        ambiguous.Index.ShouldBe(0);
        ambiguous.Reason.ShouldBe(GdsFreePinSkipReason.AmbiguousNearestPartner);
        ambiguous.NearestDistanceUm.ShouldBe(100.0, 1e-9);
        ambiguous.SecondNearestDistanceUm.ShouldBe(100.5, 1e-9);
    }

    [Fact]
    public void Pair_ClearlyNearerCandidate_IsNotAmbiguous()
    {
        // 1.5 µm delta (≥ AmbiguityDeltaUm) — the nearest candidate wins outright.
        var pairing = GdsFreePinPairer.Pair(
            new[]
            {
                Pin("a.out", 0, 0, 0, owner: 0),
                Pin("b.in", 100.0, 0, 180, owner: 1),
                Pin("c.in", 101.5, 0, 180, owner: 2),
            },
            radiusUm: 1000);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        (pair.A, pair.B).ShouldBe((0, 1));
    }

    [Fact]
    public void Pair_OnePartnerPerPin()
    {
        // b.in is the only opposing partner for both a.out and c.out — first come, first served.
        var pairing = GdsFreePinPairer.Pair(
            new[]
            {
                Pin("a.out", 0, 0, 0, owner: 0),
                Pin("b.in", 100, 0, 180, owner: 1),
                Pin("c.out", 200, 0, 0, owner: 2),
            },
            radiusUm: 1000);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        (pair.A, pair.B).ShouldBe((0, 1));
        pairing.Skipped.ShouldHaveSingleItem().Index.ShouldBe(2, "c.out loses b.in to the earlier a.out");
    }

    [Fact]
    public void Pair_SameInstancePins_NeverPair()
    {
        // A two-port component's own in/out oppose each other — they must not pair.
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.in", 0, 2, 180, owner: 0), Pin("a.out", 10, 2, 0, owner: 0) },
            radiusUm: 1000);

        pairing.Pairs.ShouldBeEmpty();
        pairing.Skipped.Count.ShouldBe(2);
        pairing.Skipped.ShouldAllBe(s => s.Reason == GdsFreePinSkipReason.NoOpposingPartnerInRadius);
    }

    [Fact]
    public void Pair_DiagonalOpposingPins_PairByEuclideanDistance()
    {
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", 30, 40, 180, owner: 1) },
            radiusUm: 60);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        pair.DistanceUm.ShouldBe(50, 1e-9);
    }

    // ── Facing check (wrap-around guard) ─────────────────────────────────────

    [Fact]
    public void Pair_PartnerBehindThePin_DoesNotPair()
    {
        // b.in opposes a.out angle-wise (180° difference) but lies BEHIND it:
        // a.out points east while b.in sits to the west — both pins point away
        // from each other (the free ends of a waveguide chain).
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", -100, 0, 180, owner: 1) },
            radiusUm: 1000);

        pairing.Pairs.ShouldBeEmpty();
        pairing.Skipped.Count.ShouldBe(2);
        pairing.Skipped.ShouldAllBe(s => s.Reason == GdsFreePinSkipReason.NotFacingEachOther);
    }

    [Fact]
    public void Pair_PartnerExactly90DegreesOffAxis_DoesNotPair()
    {
        // Displacement perpendicular to the outward direction: the dot product
        // is exactly 0, and the check requires strictly positive.
        var pairing = GdsFreePinPairer.Pair(
            new[] { Pin("a.out", 0, 0, 0, owner: 0), Pin("b.in", 0, 100, 180, owner: 1) },
            radiusUm: 1000);

        pairing.Pairs.ShouldBeEmpty();
        pairing.Skipped.Count.ShouldBe(2);
        pairing.Skipped.ShouldAllBe(s => s.Reason == GdsFreePinSkipReason.NotFacingEachOther);
    }

    [Fact]
    public void Pair_FacingCandidateWinsOverNearerBehindCandidate()
    {
        // b.in is NEARER than c.in but behind a.out — the facing c.in must pair.
        var pairing = GdsFreePinPairer.Pair(
            new[]
            {
                Pin("a.out", 0, 0, 0, owner: 0),
                Pin("b.in", -50, 0, 180, owner: 1),
                Pin("c.in", 100, 0, 180, owner: 2),
            },
            radiusUm: 1000);

        var pair = pairing.Pairs.ShouldHaveSingleItem();
        (pair.A, pair.B).ShouldBe((0, 2));
        // b.in's only opposing partner (a.out) is taken by the time it is
        // considered, so it reports the plain no-partner reason.
        pairing.Skipped.ShouldHaveSingleItem().Reason.ShouldBe(GdsFreePinSkipReason.NoOpposingPartnerInRadius);
    }
}

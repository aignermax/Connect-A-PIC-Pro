using CAP_Core.Components.Core;
using CAP_Core.Routing.AutoConnect;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.AutoConnect;

/// <summary>
/// Tests for <see cref="FacingPinPairFinder"/>: mutual-facing detection
/// (opposing angles AND each pin in front of the other), signal-domain
/// separation, greedy nearest-first assignment, and unpaired reporting.
/// </summary>
public class FacingPinPairFinderTests
{
    private static FacingPinCandidate Candidate(
        string name, double x, double y, double angleDegrees, bool isElectrical = false) =>
        new(new PhysicalPin { Name = name }, x, y, angleDegrees, isElectrical);

    [Fact]
    public void FindPairs_TwoFacingPins_PairWithTheirDistance()
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("right", 0, 0, angleDegrees: 0),
            Candidate("left", 10, 0, angleDegrees: 180),
        });

        var pair = result.Pairs.ShouldHaveSingleItem();
        pair.DistanceUm.ShouldBe(10, 1e-9);
        result.UnpairedPins.ShouldBeEmpty();
    }

    [Fact]
    public void FindPairs_BackToBackPins_StayUnpaired()
    {
        var finder = new FacingPinPairFinder();

        // Angles oppose, but each pin points AWAY from the other.
        var result = finder.FindPairs(new[]
        {
            Candidate("a", 0, 0, angleDegrees: 180),
            Candidate("b", 10, 0, angleDegrees: 0),
        });

        result.Pairs.ShouldBeEmpty();
        result.UnpairedPins.Count.ShouldBe(2);
    }

    [Fact]
    public void FindPairs_SameDirectionPins_StayUnpaired()
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("a", 0, 0, angleDegrees: 0),
            Candidate("b", 10, 0, angleDegrees: 0),
        });

        result.Pairs.ShouldBeEmpty();
        result.UnpairedPins.Count.ShouldBe(2);
    }

    [Fact]
    public void FindPairs_CoincidentPins_StayUnpaired()
    {
        var finder = new FacingPinPairFinder();

        // A perfect abutment (zero forward projection) is the import matcher's
        // job — auto-connect must not create a degenerate zero-length route.
        var result = finder.FindPairs(new[]
        {
            Candidate("a", 5, 5, angleDegrees: 0),
            Candidate("b", 5, 5, angleDegrees: 180),
        });

        result.Pairs.ShouldBeEmpty();
        result.UnpairedPins.Count.ShouldBe(2);
    }

    [Fact]
    public void FindPairs_MixedSignalDomains_NeverPairAcrossDomains()
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("optical", 0, 0, angleDegrees: 0, isElectrical: false),
            Candidate("electrical", 10, 0, angleDegrees: 180, isElectrical: true),
        });

        result.Pairs.ShouldBeEmpty();
        result.UnpairedPins.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(140, true)]  // 40° off perfect opposition — inside the 45° tolerance
    [InlineData(120, false)] // 60° off — outside
    public void FindPairs_OppositionTolerance_Is45Degrees(double partnerAngle, bool shouldPair)
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("a", 0, 0, angleDegrees: 0),
            Candidate("b", 10, 0, partnerAngle),
        });

        result.Pairs.Any().ShouldBe(shouldPair);
    }

    [Fact]
    public void FindPairs_MultipleCandidates_AssignsNearestPartnersFirst()
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("r1", 0, 0, angleDegrees: 0),
            Candidate("r2", 0, 10, angleDegrees: 0),
            Candidate("l1", 5, 0, angleDegrees: 180),
            Candidate("l2", 5, 10, angleDegrees: 180),
        });

        result.Pairs.Count.ShouldBe(2);
        result.UnpairedPins.ShouldBeEmpty();
        // Each right pin gets its CLOSEST facing partner, not the diagonal one.
        result.Pairs.Select(p => (p.A.Name, p.B.Name))
            .ShouldBe(new[] { ("r1", "l1"), ("r2", "l2") }, ignoreOrder: true);
        result.Pairs.ShouldAllBe(p => Math.Abs(p.DistanceUm - 5) < 1e-9);
    }

    [Fact]
    public void FindPairs_LeftoverPin_IsReportedUnpaired()
    {
        var finder = new FacingPinPairFinder();

        var result = finder.FindPairs(new[]
        {
            Candidate("r1", 0, 0, angleDegrees: 0),
            Candidate("l1", 5, 0, angleDegrees: 180),
            Candidate("l2", 20, 0, angleDegrees: 180),
        });

        result.Pairs.ShouldHaveSingleItem();
        result.UnpairedPins.ShouldHaveSingleItem().Name.ShouldBe("l2");
    }
}

using CAP.Avalonia.Services.GdsImport;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests <see cref="GdsPlacementExecutor.ShouldLiveRoute"/> (issue #854): imported
/// METAL route-derived connections are live-routed whenever re-routing is requested
/// — exempt from the optical re-route cap, because traced straight-cornered metal
/// outlines are electrically unacceptable at RF — while optical connections follow
/// the capped per-import decision, and abutment pairs are never live-routed.
/// </summary>
public class GdsMetalLiveRouteTests
{
    private static GdsConnectionInstruction Connection(bool isRouteDerived, bool isElectrical) => new()
    {
        A = new GdsConnectionEndpoint { InstanceIndex = 0, PinName = "a" },
        B = new GdsConnectionEndpoint { InstanceIndex = 1, PinName = "b" },
        IsRouteDerived = isRouteDerived,
        IsElectrical = isElectrical,
    };

    [Fact]
    public void ShouldLiveRoute_MetalRouteDerived_ExemptFromOpticalCap()
    {
        var metal = Connection(isRouteDerived: true, isElectrical: true);

        // rerouteOptical=false simulates the cap kicking in — metal still live-routes.
        GdsPlacementExecutor.ShouldLiveRoute(metal, rerouteOptical: false, rerouteRequested: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldLiveRoute_MetalRouteDerived_RespectsRerouteToggleOff()
    {
        var metal = Connection(isRouteDerived: true, isElectrical: true);

        GdsPlacementExecutor.ShouldLiveRoute(metal, rerouteOptical: false, rerouteRequested: false)
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldLiveRoute_OpticalRouteDerived_FollowsCappedDecision(bool rerouteOptical)
    {
        var optical = Connection(isRouteDerived: true, isElectrical: false);

        GdsPlacementExecutor.ShouldLiveRoute(optical, rerouteOptical, rerouteRequested: true)
            .ShouldBe(rerouteOptical);
    }

    [Fact]
    public void ShouldLiveRoute_AbutmentPair_NeverLiveRouted()
    {
        var abutment = Connection(isRouteDerived: false, isElectrical: true);

        GdsPlacementExecutor.ShouldLiveRoute(abutment, rerouteOptical: true, rerouteRequested: true)
            .ShouldBeFalse();
    }
}

using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Field-report regression (direct-route policy, issue #860 follow-up): an MMI rotated
/// 180° faces its <c>in</c> pin AWAY from the grating coupler it connects to. The styled
/// S-bend candidate ignores the end pin's outward direction and arrives through the
/// coupler's body "as if everything was fine". The router must notice the body crossing
/// and hand the connection to A*, which routes around the component instead.
/// Fixture: the real PDK templates at the reported coordinates.
/// </summary>
public class DirectRouteBodyCrossingTests
{
    [Fact]
    public void Route_PinFacesAway_StyledPathThroughTargetBody_FallsBackToAStar()
    {
        var mmiTemplate = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .First(t => t.NazcaFunctionName == "demo.mmi1x2_sh");
        var gcTemplate = TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json")
            .First(t => t.NazcaFunctionName == "ebeam_gc_te895");

        var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, 1219.227, -623.007);
        var mmi = ComponentTemplates.CreateFromTemplate(mmiTemplate, 1057.015, -584.181);
        ComponentPoseTransform.Rotate90CounterClockwise(mmi);
        ComponentPoseTransform.Rotate90CounterClockwise(mmi);

        var start = mmi.PhysicalPins.First(p => p.Name == "in");
        var end = gc.PhysicalPins.First(p => p.Name == "port 2");

        var router = new WaveguideRouter
        {
            MinBendRadiusMicrometers = 10.0,
            MinWaveguideSpacingMicrometers = 2.0,
        };
        router.InitializePathfindingGrid(1000, -700, 1450, -480, new[] { gc, mmi });

        var path = router.Route(start, end);

        path.IsDirectStyledRoute.ShouldBeFalse(
            "the styled candidate enters the east-facing port through the coupler body — " +
            "it must defer to A*, not flow through the component as if everything was fine");
        path.IsBlockedFallback.ShouldBeFalse("A* routes around the coupler in open space");
        router.IsPathBlocked(path.Segments).ShouldBeFalse();
        CrossesBody(path, gc).ShouldBeFalse("the route must not cross the grating coupler's body");
        CrossesBody(path, mmi).ShouldBeFalse("the route must not cross the MMI's body");
    }

    /// <summary>True when any mid-path sample lands strictly inside the component's bbox.</summary>
    private static bool CrossesBody(RoutedPath path, Component body)
    {
        foreach (var seg in path.Segments)
        {
            for (double t = 0.02; t < 0.98; t += 0.01)
            {
                double x = seg.StartPoint.X + (seg.EndPoint.X - seg.StartPoint.X) * t;
                double y = seg.StartPoint.Y + (seg.EndPoint.Y - seg.StartPoint.Y) * t;
                if (x > body.PhysicalX + 0.5 && x < body.PhysicalX + body.WidthMicrometers - 0.5 &&
                    y > body.PhysicalY + 0.5 && y < body.PhysicalY + body.HeightMicrometers - 0.5)
                    return true;
            }
        }
        return false;
    }
}

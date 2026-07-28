using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using UnitTests.Routing.CrossingInsertion;

namespace UnitTests.Helpers;

/// <summary>
/// Builds the standard Cut-tool test scene shared by the UI-flow and walkthrough-screenshot
/// tests: a horizontal waveguide (10,100)→(390,100) plus a guide terminal above it whose
/// south-facing pin ray crosses the waveguide at (200, 100).
/// </summary>
internal static class CutToolTestScene
{
    /// <summary>Adds the three terminals and the connection to <paramref name="vm"/>'s canvas.</summary>
    public static WaveguideConnection Build(MainViewModel vm)
    {
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var guide = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        vm.Canvas.AddComponent(left.Component, "Terminal");
        vm.Canvas.AddComponent(right.Component, "Terminal");
        vm.Canvas.AddComponent(guide.Component, "Terminal");

        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        return vm.Canvas.ConnectPinsWithCachedRoute(left.PhysicalPin, right.PhysicalPin, route)!.Connection;
    }
}

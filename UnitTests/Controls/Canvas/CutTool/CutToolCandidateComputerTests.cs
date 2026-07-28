using Avalonia;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Canvas.CutTool;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using UnitTests.Routing.CrossingInsertion;
using Xunit;

namespace UnitTests.Controls.Canvas.CutTool;

/// <summary>
/// Tests for <see cref="CutToolCandidateComputer"/>'s eligibility, template-validation and
/// dirty-flag guards, shared by the ambient candidate sweep and the free-cut fallback.
/// </summary>
public class CutToolCandidateComputerTests
{
    [Fact]
    public void CollectEligibleConnections_ExcludesSubsOfInsertedCrossing_EvenWhenAdaptiveServiceIsNull()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.ConnectionManager.CrossingInsertion = null; // adaptive feature disabled/not wired

        var crossing = CrossingTestCircuit.CreateCrossingComponent();
        crossing.IsInsertedCrossing = true;
        var west = crossing.PhysicalPins.Single(p => p.Name == "port 1");
        var east = crossing.PhysicalPins.Single(p => p.Name == "port 2");
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);

        var subA = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = west };
        var subB = new WaveguideConnection { StartPin = east, EndPin = right.PhysicalPin };
        canvas.ConnectionManager.AddExistingConnection(subA);
        canvas.ConnectionManager.AddExistingConnection(subB);
        canvas.Connections.Add(new WaveguideConnectionViewModel(subA));
        canvas.Connections.Add(new WaveguideConnectionViewModel(subB));

        var eligible = CutToolCandidateComputer.CollectEligibleConnections(canvas).ToList();

        eligible.ShouldBeEmpty(
            "a connection docked on an IsInsertedCrossing component must never be cuttable, " +
            "regardless of whether the adaptive service is running");
    }

    [Fact]
    public void CollectEligibleConnections_IncludesOrdinaryConnection()
    {
        var canvas = new DesignCanvasViewModel();
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var connection = new WaveguideConnection { StartPin = left.PhysicalPin, EndPin = right.PhysicalPin };
        canvas.ConnectionManager.AddExistingConnection(connection);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));

        var eligible = CutToolCandidateComputer.CollectEligibleConnections(canvas).ToList();

        eligible.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact]
    public void FindUsableCrossingTemplate_ReturnsTemplate_WhenAllFourPortsWired()
    {
        var template = BuildCrossingTemplate(portCount: 4);

        var result = CutToolCandidateComputer.FindUsableCrossingTemplate(new[] { template });

        result.ShouldBeSameAs(template);
    }

    [Fact]
    public void FindUsableCrossingTemplate_ReturnsNull_WhenAPortIsMissing()
    {
        var template = BuildCrossingTemplate(portCount: 3); // missing the 4th (north) port

        var result = CutToolCandidateComputer.FindUsableCrossingTemplate(new[] { template });

        result.ShouldBeNull(
            "a template missing a wired port must be treated as unusable up front, not fail " +
            "dishonestly after the user already clicked");
    }

    [Fact]
    public void Update_SkipsRecompute_WhenViewportAndDesignUnchanged_ButRecomputesAfterAChange()
    {
        var canvas = new DesignCanvasViewModel();
        var left = CrossingTestCircuit.CreateTerminal("left", 0, 95, pinAngleDegrees: 0);
        var right = CrossingTestCircuit.CreateTerminal("right", 380, 95, pinAngleDegrees: 180);
        var guide = CrossingTestCircuit.CreateTerminal("guide", 195, 40, pinAngleDegrees: 90);
        canvas.AddComponent(left.Component, "Terminal");
        canvas.AddComponent(right.Component, "Terminal");
        canvas.AddComponent(guide.Component, "Terminal");
        var route = new RoutedPath();
        route.Segments.Add(new StraightSegment(10, 100, 390, 100, 0));
        canvas.ConnectPinsWithCachedRoute(left.PhysicalPin, right.PhysicalPin, route);

        var mainVm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        var templates = TestPdkLoader.LoadFromPdk("siepic-ebeam-pdk.json");
        mainVm.LeftPanel.AllTemplates.Add(templates.Single(t => string.Equals(
            t.NazcaFunctionName, CrossingComponentInstance.CrossingNazcaFunctionName,
            StringComparison.OrdinalIgnoreCase)));

        var state = new CanvasInteractionState();
        var computer = new CutToolCandidateComputer();
        var viewport = new Rect(-20, 20, 440, 220);

        computer.Update(state, canvas, mainVm, viewport);
        var firstCandidates = state.CutCandidates;
        firstCandidates.ShouldNotBeEmpty();

        computer.Update(state, canvas, mainVm, viewport);
        state.CutCandidates.ShouldBeSameAs(firstCandidates,
            "an unchanged viewport and design must skip the expensive guide/segment sweep");

        var extra = CrossingTestCircuit.CreateTerminal("extra", 350, 300, pinAngleDegrees: 0);
        canvas.AddComponent(extra.Component, "Terminal");
        computer.Update(state, canvas, mainVm, viewport);

        state.CutCandidates.ShouldNotBeSameAs(firstCandidates,
            "adding a component changes the design content signature and must trigger a fresh sweep");
    }

    /// <summary>Builds a template shaped like the PDK crossing with only the first
    /// <paramref name="portCount"/> of its four cardinal ports (E/S/W/N order).</summary>
    private static ComponentTemplate BuildCrossingTemplate(int portCount)
    {
        var angles = new[] { 0.0, 90.0, 180.0, 270.0 };
        var pins = new PinDefinition[portCount];
        for (int i = 0; i < portCount; i++)
            pins[i] = new PinDefinition($"port {i + 1}", 0, 0, angles[i]);

        return new ComponentTemplate
        {
            Name = "Crossing 4-Port",
            NazcaFunctionName = CrossingComponentInstance.CrossingNazcaFunctionName,
            WidthMicrometers = 9.7,
            HeightMicrometers = 9.7,
            PinDefinitions = pins,
            CreateSMatrix = logicalPins => new CAP_Core.LightCalculation.SMatrix(
                logicalPins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(), new()),
        };
    }
}

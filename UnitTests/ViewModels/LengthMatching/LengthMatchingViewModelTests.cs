using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.LengthMatching;

/// <summary>
/// Integration tests for the "Length matching" Properties-panel section: selecting a
/// single waveguide connection shows its current length, Apply stretches the route to an
/// exact target length via a meander (frozen afterwards), typed matcher failures surface
/// as readable translated messages (never raw enum names), and Clear drops the intent and
/// hands the route back to the normal router.
/// </summary>
public class LengthMatchingViewModelTests
{
    private const double LengthTolerance = 0.5;

    private static (PhysicalPin startPin, PhysicalPin endPin) AddComponentPair(
        DesignCanvasViewModel canvas, double offsetY = 0)
    {
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        startComp.PhysicalX = 0;
        startComp.PhysicalY = offsetY;

        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = offsetY;

        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        return (startComp.PhysicalPins.First(p => p.Name == "out"),
                endComp.PhysicalPins.First(p => p.Name == "in"));
    }

    /// <summary>Connects the two facing pins with the plain straight route between them.</summary>
    private static WaveguideConnectionViewModel ConnectStraight(
        DesignCanvasViewModel canvas, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
        var vm = canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
        vm.ShouldNotBeNull();
        return vm!;
    }

    [Fact]
    public void SelectedConnection_PopulatesCurrentLength_AndDefaults()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);

        var vm = new LengthMatchingViewModel(canvas);
        vm.HasExactlyOneConnection.ShouldBeFalse();

        vm.SelectedConnection = connVm;

        vm.HasExactlyOneConnection.ShouldBeTrue();
        vm.CurrentLengthMicrometers.ShouldBe(connVm.Connection.PathLengthMicrometers, 1e-6);
        double.Parse(vm.TargetLengthText, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(connVm.Connection.PathLengthMicrometers, 1e-6);
        vm.ToleranceText.ShouldBe("0.1");
    }

    [Fact]
    public void HasExactlyOneConnection_BatchOfOne_TargetsThatConnection()
    {
        var canvas = new DesignCanvasViewModel();
        var (startA, endA) = AddComponentPair(canvas, 0);
        var (startB, endB) = AddComponentPair(canvas, 2000);
        var connA = ConnectStraight(canvas, startA, endA);
        var connB = ConnectStraight(canvas, startB, endB);

        var vm = new LengthMatchingViewModel(canvas);
        vm.HasExactlyOneConnection.ShouldBeFalse("nothing selected yet");

        canvas.Selection.SelectedConnections.Add(connA);
        canvas.Selection.SelectedConnections.Add(connB);
        vm.HasExactlyOneConnection.ShouldBeFalse("a multi-connection batch is not supported");

        canvas.Selection.SelectedConnections.Remove(connB);
        vm.HasExactlyOneConnection.ShouldBeTrue("a batch of exactly one targets that connection");
        vm.CurrentLengthMicrometers.ShouldBe(connA.Connection.PathLengthMicrometers, 1e-6);
    }

    [Fact]
    public void Apply_ReachableTarget_MeandersFreezes_AndReportsSuccess()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);
        double directLength = connVm.Connection.PathLengthMicrometers;
        double target = directLength * 3;

        var vm = new LengthMatchingViewModel(canvas) { SelectedConnection = connVm };
        vm.TargetLengthText = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ToleranceText = "0.1";

        vm.ApplyCommand.Execute(null);

        connVm.Connection.TargetLengthMicrometers.ShouldBe(target);
        connVm.Connection.LengthToleranceMicrometers.ShouldBe(0.1);
        connVm.Connection.IsRouteFrozen.ShouldBeTrue(
            "the meandered geometry must survive later recalculations while the endpoints stay put");
        connVm.Connection.PathLengthMicrometers.ShouldBe(target, LengthTolerance);
        vm.CurrentLengthMicrometers.ShouldBe(connVm.Connection.PathLengthMicrometers, 1e-6);
        vm.IsStatusError.ShouldBeFalse();
        vm.HasSuccessStatus.ShouldBeTrue();
        vm.StatusMessage.ShouldBe(string.Format(
            LocalizationService.Instance.Translate("Routing.LengthMatch.Success"),
            vm.CurrentLengthMicrometers));
    }

    [Fact]
    public void Apply_TargetShorterThanDirectPath_ShowsTranslatedError_AndLeavesRouteUntouched()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);
        var routeBefore = connVm.Connection.RoutedPath;

        var vm = new LengthMatchingViewModel(canvas) { SelectedConnection = connVm };
        vm.TargetLengthText = (connVm.Connection.PathLengthMicrometers / 2)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ToleranceText = "0.1";

        vm.ApplyCommand.Execute(null);

        vm.IsStatusError.ShouldBeTrue();
        vm.HasErrorStatus.ShouldBeTrue();
        vm.StatusMessage.ShouldBe(
            LocalizationService.Instance.Translate("Routing.LengthMatch.Error.TargetShorterThanDirectPath"));
        // The raw enum name must never surface in the UI.
        vm.StatusMessage.ShouldNotContain(nameof(MeanderFailureReason.TargetShorterThanDirectPath));
        connVm.Connection.RoutedPath.ShouldBeSameAs(routeBefore);
        connVm.Connection.TargetLengthMicrometers.ShouldBeNull();
        connVm.Connection.LengthToleranceMicrometers.ShouldBeNull();
        connVm.Connection.IsRouteFrozen.ShouldBeFalse();
    }

    [Fact]
    public void Apply_InvalidInput_ShowsTranslatedError_AndLeavesRouteUntouched()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);
        var routeBefore = connVm.Connection.RoutedPath;

        var vm = new LengthMatchingViewModel(canvas) { SelectedConnection = connVm };
        vm.TargetLengthText = "not-a-number";

        vm.ApplyCommand.Execute(null);

        vm.IsStatusError.ShouldBeTrue();
        vm.StatusMessage.ShouldBe(
            LocalizationService.Instance.Translate("Routing.LengthMatch.Error.InvalidInput"));
        connVm.Connection.RoutedPath.ShouldBeSameAs(routeBefore);
        connVm.Connection.TargetLengthMicrometers.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_AfterSuccessfulApply_ResetsTargetAndUnfreezes()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);
        double directLength = connVm.Connection.PathLengthMicrometers;

        var vm = new LengthMatchingViewModel(canvas) { SelectedConnection = connVm };
        vm.TargetLengthText = (directLength * 3)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ApplyCommand.Execute(null);
        connVm.Connection.IsRouteFrozen.ShouldBeTrue();

        await vm.ClearCommand.ExecuteAsync(null);

        connVm.Connection.TargetLengthMicrometers.ShouldBeNull();
        connVm.Connection.LengthToleranceMicrometers.ShouldBeNull();
        connVm.Connection.IsRouteFrozen.ShouldBeFalse(
            "clearing hands the route back to the normal router");
        connVm.Connection.RoutedPath.ShouldNotBeNull("Clear triggers a canvas re-route");
        vm.IsStatusError.ShouldBeFalse();
        vm.HasSuccessStatus.ShouldBeTrue();
    }

    [Fact]
    public async Task Clear_WithoutTargetOnFrozenRoute_KeepsGeometryUntouched()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas);
        var connVm = ConnectStraight(canvas, startPin, endPin);
        var routeBefore = connVm.Connection.RoutedPath;
        // GDS-imported routes are frozen with no length target (GdsPlacementExecutor).
        connVm.Connection.IsRouteFrozen = true;

        var vm = new LengthMatchingViewModel(canvas) { SelectedConnection = connVm };
        await vm.ClearCommand.ExecuteAsync(null);

        connVm.Connection.IsRouteFrozen.ShouldBeTrue(
            "Clear must not unfreeze a route that never had a length target — " +
            "unfreezing a GDS-imported route would silently discard its imported geometry");
        connVm.Connection.RoutedPath.ShouldBeSameAs(routeBefore);
        connVm.Connection.TargetLengthMicrometers.ShouldBeNull();
        connVm.Connection.LengthToleranceMicrometers.ShouldBeNull();
        vm.HasStatus.ShouldBeFalse("nothing was cleared, so no status is shown");
    }
}

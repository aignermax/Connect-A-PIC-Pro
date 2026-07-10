using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Tiles;
using Shouldly;

namespace UnitTests.Analysis;

/// <summary>
/// Tests for the #690 laser on/off toggle in <see cref="TransientCircuitFactory"/>:
/// disabled lasers must not inject light, and their pins must be reported as the
/// design's output-coupler pins.
/// </summary>
public class TransientCircuitFactoryLaserToggleTests
{
    private const string CouplerTemplate = "Grating Coupler";

    private static (DesignCanvasViewModel Canvas, ComponentViewModel Coupler) CreateCanvasWithCoupler()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = canvas.AddComponent(CreateCouplerComponent(), CouplerTemplate);
        return (canvas, vm);
    }

    /// <summary>Straight-waveguide component with physical pins attached to both logical pins.</summary>
    private static Component CreateCouplerComponent()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        var west = component.Parts[0, 0].GetPinAt(RectSide.Left);
        var east = component.Parts[0, 0].GetPinAt(RectSide.Right);
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "west0", ParentComponent = component,
            OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180, LogicalPin = west
        });
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "east0", ParentComponent = component,
            OffsetXMicrometers = 20, OffsetYMicrometers = 5, AngleDegrees = 0, LogicalPin = east
        });
        return component;
    }

    [Fact]
    public void CouplerTemplate_GetsLaserConfig_EnabledByDefault()
    {
        var (_, coupler) = CreateCanvasWithCoupler();

        coupler.LaserConfig.ShouldNotBeNull();
        coupler.LaserConfig!.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Create_RegistersLightSources_WhenLaserEnabled()
    {
        var (canvas, _) = CreateCanvasWithCoupler();

        var (_, ports) = TransientCircuitFactory.Create(canvas);

        ports.GetAllExternalInputs().ShouldNotBeEmpty();
    }

    [Fact]
    public void Create_SkipsCoupler_WhenLaserDisabled()
    {
        var (canvas, coupler) = CreateCanvasWithCoupler();
        coupler.LaserConfig!.IsEnabled = false;

        var (_, ports) = TransientCircuitFactory.Create(canvas);

        ports.GetAllExternalInputs().ShouldBeEmpty();
    }

    [Fact]
    public void Create_KeepsEnabledCoupler_WhenAnotherIsDisabled()
    {
        var (canvas, disabled) = CreateCanvasWithCoupler();
        disabled.LaserConfig!.IsEnabled = false;
        var enabled = canvas.AddComponent(CreateCouplerComponent(), CouplerTemplate);
        var enabledLightPinCount = enabled.Component.PhysicalPins
            .Count(p => p.LogicalPin?.MatterType == MatterType.Light);

        var (_, ports) = TransientCircuitFactory.Create(canvas);

        // Only the enabled coupler's light pins get a source; the disabled one is skipped.
        ports.GetAllExternalInputs().Count.ShouldBe(enabledLightPinCount);
    }

    [Fact]
    public void CollectOutputCouplerPinIds_IsEmpty_WhenAllLasersOn()
    {
        var (canvas, _) = CreateCanvasWithCoupler();

        TransientCircuitFactory.CollectOutputCouplerPinIds(canvas).ShouldBeEmpty();
    }

    [Fact]
    public void CollectOutputCouplerPinIds_ReturnsBothFlowIds_OfDisabledCoupler()
    {
        var (canvas, coupler) = CreateCanvasWithCoupler();
        coupler.LaserConfig!.IsEnabled = false;

        var pinIds = TransientCircuitFactory.CollectOutputCouplerPinIds(canvas);

        pinIds.ShouldNotBeEmpty();
        foreach (var pin in coupler.Component.PhysicalPins)
        {
            if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
            pinIds.ShouldContain(pin.LogicalPin.IDInFlow);
            pinIds.ShouldContain(pin.LogicalPin.IDOutFlow);
        }
    }

    [Fact]
    public void LaserToggle_RoundTrips_OnTheSharedConfigInstance()
    {
        var (_, coupler) = CreateCanvasWithCoupler();
        var config = coupler.LaserConfig!;

        config.IsEnabled = false;
        coupler.LaserConfig!.IsEnabled.ShouldBeFalse();

        config.IsEnabled = true;
        coupler.LaserConfig!.IsEnabled.ShouldBeTrue();
    }
}

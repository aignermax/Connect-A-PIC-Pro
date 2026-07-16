using CAP.Avalonia.Commands;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using Shouldly;

namespace UnitTests.Simulation;

/// <summary>
/// Lifecycle tests for the #690 laser on/off role: the state lives on the core
/// <see cref="Component"/> so grouping, ungrouping and delete/undo (which all
/// recreate the ViewModel) cannot silently flip an output coupler back into an
/// input. Also covers the undoable canvas toggle and the icon logic.
/// </summary>
public class LaserToggleLifecycleTests
{
    private const string CouplerTemplate = "Grating Coupler";

    private static (DesignCanvasViewModel Canvas, ComponentViewModel Coupler) CreateCanvasWithCoupler()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide(), CouplerTemplate);
        return (canvas, vm);
    }

    [Fact]
    public void LaserOff_SurvivesViewModelRecreation()
    {
        var (canvas, coupler) = CreateCanvasWithCoupler();
        coupler.LaserConfig!.IsEnabled = false;

        // Grouping/ungrouping/delete-undo all rebuild the VM around the same core
        // component — the role must come back with it.
        var recreated = new ComponentViewModel(coupler.Component, CouplerTemplate);

        recreated.LaserConfig.ShouldNotBeNull();
        recreated.IsLaserOff.ShouldBeTrue();
        coupler.Component.LaserEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Coupler_WithoutTemplateName_StillGetsLaserConfig_ViaComponentClassification()
    {
        // Ungrouping re-adds children without a template name; the classifier must
        // recognise the coupler from its PDK-derived identity instead.
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.NazcaFunctionName = "ebeam_gc_te1550";

        var vm = new ComponentViewModel(component, templateName: null);

        vm.LaserConfig.ShouldNotBeNull();
        LightSourceClassifier.IsLightInjectingCoupler(component).ShouldBeTrue();
    }

    [Fact]
    public void ToggleLaserCommand_ExecuteAndUndo_RestoreTheRole()
    {
        var (_, coupler) = CreateCanvasWithCoupler();

        var command = new ToggleLaserCommand(coupler);
        command.Execute();
        coupler.IsLaserOff.ShouldBeTrue();

        command.Undo();
        coupler.IsLaserOff.ShouldBeFalse();
        coupler.Component.LaserEnabled.ShouldBeTrue();
    }

    [Fact]
    public void OffIcon_StaysVisibleOutsideSimulationMode_NoOneWayTrap()
    {
        var (_, coupler) = CreateCanvasWithCoupler();
        coupler.LaserConfig!.IsEnabled = false;

        // Regression for the one-way trap: the off icon used to vanish outside a
        // simulation mode, so a single click removed the control under the cursor.
        LaserIndicatorRenderer.IsIconVisible(coupler, simulationActive: false).ShouldBeTrue();
        LaserIndicatorRenderer.IsIconVisible(coupler, simulationActive: true).ShouldBeTrue();
    }

    [Fact]
    public void NonLightSource_HasNoIcon()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide(), "Straight Waveguide");

        LaserIndicatorRenderer.IsIconVisible(vm, simulationActive: true).ShouldBeFalse();
    }

    [Fact]
    public void IconBounds_ClampToMinimumSize_ForTinyComponents()
    {
        var (_, coupler) = CreateCanvasWithCoupler();

        var bounds = LaserIndicatorRenderer.CalculateIconBounds(coupler);

        bounds.Width.ShouldBeGreaterThanOrEqualTo(12);
        bounds.Width.ShouldBeLessThanOrEqualTo(24);
        bounds.X.ShouldBe(coupler.X + 4);
        bounds.Y.ShouldBe(coupler.Y + 4);
    }

    [Fact]
    public void WavelengthColor_FollowsTheSimulationClassification()
    {
        var green = LaserIndicatorRenderer.GetWavelengthColor(StandardWaveLengths.GreenNM);
        var blue = LaserIndicatorRenderer.GetWavelengthColor(StandardWaveLengths.BlueNM);
        var red = LaserIndicatorRenderer.GetWavelengthColor(StandardWaveLengths.RedNM);
        var unknown = LaserIndicatorRenderer.GetWavelengthColor(1234);

        green.ShouldNotBe(blue);
        red.ShouldBe(unknown); // unknown wavelengths fall back to red, like the simulation
    }
}

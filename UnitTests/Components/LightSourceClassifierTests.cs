using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.ComponentHelpers;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Regression tests for Issue #689: PDK coupler template names with suffixes
/// (e.g. "Grating Coupler TE 1550") must be classified as light sources so the
/// wavelength/power editor appears and the transient simulation injects light.
/// </summary>
public class LightSourceClassifierTests
{
    [Theory]
    // Fiber-interface couplers — the only true light-injection points.
    [InlineData("Grating Coupler", true)]
    [InlineData("Edge Coupler", true)]
    [InlineData("Grating Coupler TE 1550", true)]
    [InlineData("Grating Coupler TE 1310", true)]
    [InlineData("Grating Coupler TE 895", true)]
    [InlineData("grating coupler te 1550", true)]
    [InlineData("Grating Coupler Elliptical", true)]
    [InlineData("Grating Coupler Rectangular", true)]
    // On-chip splitters that also carry "Coupler" in their name — passive
    // components, never light sources (misclassified before this fix).
    [InlineData("Directional Coupler", false)]
    [InlineData("Directional Coupler TE 1550", false)]
    [InlineData("Directional Coupler TE 1550 (Lc=5um)", false)]
    [InlineData("Contra-Directional Coupler", false)]
    [InlineData("adiabatic coupler TE1550", false)]
    [InlineData("Adiabatic Coupler TM 1550", false)]
    [InlineData("2x2 MMI Coupler", false)]
    [InlineData("Coupler", false)]
    [InlineData("Coupler Straight", false)]
    // Non-couplers
    [InlineData("Straight Waveguide", false)]
    [InlineData("Phase Shifter", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsLightInjectingCoupler_ClassifiesTemplateNames(string? templateName, bool expected)
    {
        LightSourceClassifier.IsLightInjectingCoupler(templateName).ShouldBe(expected);
    }

    [Fact]
    public void ComponentViewModel_WithSuffixedCouplerName_ExposesLaserConfig()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();

        var vm = new ComponentViewModel(component, "Grating Coupler TE 1550");

        vm.IsLightSource.ShouldBeTrue();
        vm.LaserConfig.ShouldNotBeNull();
    }

    [Fact]
    public void ComponentViewModel_WithDirectionalCoupler_HasNoLaserConfig()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();

        var vm = new ComponentViewModel(component, "Directional Coupler TE 1550");

        vm.IsLightSource.ShouldBeFalse();
        vm.LaserConfig.ShouldBeNull();
    }

    [Fact]
    public void UserMarkTurnsAnyComponentIntoALightSource_AndUnmarkRevertsIt()
    {
        // Imported GDS cells carry no role in their name — the user marks the
        // laser couplers manually (foundry field wish). The mark must create the
        // LaserConfig (editor + simulation role) and the unmark must undo it.
        var component = TestComponentFactory.CreateStraightWaveGuide();
        var vm = new ComponentViewModel(component, "nazca_foundry_cell_1234");

        vm.IsAutoClassifiedLightSource.ShouldBeFalse();
        vm.ShowLightSourceMarkToggle.ShouldBeTrue();
        vm.IsLightSource.ShouldBeFalse();

        vm.IsUserMarkedLightSource = true;

        vm.IsLightSource.ShouldBeTrue();
        vm.LaserConfig.ShouldNotBeNull("the mark must surface the laser editor");
        CAP.Avalonia.Services.SimulationService.IsLightSource(component).ShouldBeTrue(
            "the model-level gate must agree (simulation input)");

        vm.IsUserMarkedLightSource = false;

        vm.IsLightSource.ShouldBeFalse();
        vm.LaserConfig.ShouldBeNull();
        CAP.Avalonia.Services.SimulationService.IsLightSource(component).ShouldBeFalse();
    }

    [Fact]
    public void Unmarking_ANameClassifiedCoupler_KeepsTheLaserConfig()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        var vm = new ComponentViewModel(component, "Grating Coupler TE 1550");

        vm.IsUserMarkedLightSource = false;

        vm.IsLightSource.ShouldBeTrue("name-classified couplers stay sources regardless of the manual mark");
        vm.ShowLightSourceMarkToggle.ShouldBeFalse();
    }
}

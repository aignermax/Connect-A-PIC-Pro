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
    // Bundled demo PDK
    [InlineData("Grating Coupler", true)]
    [InlineData("Edge Coupler", true)]
    [InlineData("Directional Coupler", false)]
    // SiEPIC eBeam PDK
    [InlineData("Grating Coupler TE 1550", true)]
    [InlineData("Grating Coupler TE 1310", true)]
    [InlineData("Grating Coupler TE 895", true)]
    [InlineData("adiabatic coupler TE1550", true)]
    [InlineData("Adiabatic Coupler TM 1550", true)]
    [InlineData("Directional Coupler TE 1550", false)]
    [InlineData("Directional Coupler TE 1550 (Lc=5um)", false)]
    [InlineData("Contra-Directional Coupler", false)]
    // Cornerstone SiN PDK
    [InlineData("Grating Coupler Elliptical", true)]
    [InlineData("Grating Coupler Rectangular", true)]
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
}

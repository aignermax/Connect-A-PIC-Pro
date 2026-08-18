using CAP_Core.Components.Core;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Tests for <see cref="ConnectionCrossSectionResolver"/>: a connection's process
/// cross-section comes from its endpoint pins' PDK stamps (start pin first, end pin
/// as fallback), never from a global default.
/// </summary>
public class ConnectionCrossSectionResolverTests
{
    [Fact]
    public void Resolve_BothPinsStamped_StartPinWins()
    {
        var start = StampedPin(width: 1.2, layer: 203, crossSection: "xs_nc");
        var end = StampedPin(width: 0.5, layer: 1, crossSection: "strip");

        var resolved = ConnectionCrossSectionResolver.Resolve(start, end);

        resolved.WidthMicrometers.ShouldBe(1.2);
        resolved.GdsLayer.ShouldBe(203);
        resolved.GdsFactoryRoutingCrossSection.ShouldBe("xs_nc");
        resolved.HasOpticalStamps.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_StartPinUnstamped_EndPinStampsApply()
    {
        var start = StampedPin(width: null, layer: null, crossSection: null);
        var end = StampedPin(width: 0.5, layer: 1, crossSection: null);

        var resolved = ConnectionCrossSectionResolver.Resolve(start, end);

        resolved.WidthMicrometers.ShouldBe(0.5);
        resolved.GdsLayer.ShouldBe(1);
        resolved.GdsFactoryOwner.ShouldBeNull();
        resolved.HasOpticalStamps.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_CrossSectionOwnerFallsBackToEndPinParent()
    {
        var start = StampedPin(width: 1.2, layer: 203, crossSection: null);
        var end = StampedPin(width: 1.2, layer: 203, crossSection: "xs_nc");

        var resolved = ConnectionCrossSectionResolver.Resolve(start, end);

        resolved.GdsFactoryRoutingCrossSection.ShouldBe("xs_nc");
    }

    [Fact]
    public void Resolve_UnstampedPins_ReturnsEmptyCrossSection()
    {
        var resolved = ConnectionCrossSectionResolver.Resolve(
            StampedPin(width: null, layer: null, crossSection: null), null);

        resolved.WidthMicrometers.ShouldBeNull();
        resolved.GdsLayer.ShouldBeNull();
        resolved.GdsFactoryOwner.ShouldBeNull();
        resolved.HasOpticalStamps.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_NullPins_ReturnsEmptyCrossSection()
    {
        var resolved = ConnectionCrossSectionResolver.Resolve(null, null);

        resolved.HasOpticalStamps.ShouldBeFalse();
    }

    private static PhysicalPin StampedPin(double? width, int? layer, string? crossSection)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.GdsFactoryRoutingCrossSection = crossSection;
        return new PhysicalPin
        {
            Name = "o1",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 0,
            WaveguideWidthMicrometers = width,
            Layer = layer,
        };
    }
}

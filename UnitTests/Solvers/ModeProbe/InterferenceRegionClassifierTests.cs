using CAP_Core.Solvers.ModeProbe;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.ModeProbe;

public class InterferenceRegionClassifierTests
{
    [Theory]
    [InlineData("MMI 1x2")]
    [InlineData("MMI 2x2")]
    [InlineData("mmi_splitter")]
    [InlineData("Multimode Interference Coupler")]
    [InlineData("Multi-Mode Section")]
    [InlineData("Star Coupler 8x8")]
    public void MultimodeComponents_AreInterferenceRegions(string name)
    {
        InterferenceRegionClassifier.IsInterferenceRegion(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Straight Waveguide")]
    [InlineData("Directional Coupler")]
    [InlineData("Grating Coupler TE 1550")]
    [InlineData("Edge Coupler")]
    [InlineData("Ring Resonator")]
    [InlineData(null)]
    [InlineData("")]
    public void SingleModeComponents_AreNot(string? name)
    {
        InterferenceRegionClassifier.IsInterferenceRegion(name).ShouldBeFalse();
    }
}

using CAP.Avalonia.Controls.Rendering;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Matrix test for <see cref="PinGlyphSelector"/> covering every (MatterType × PolarizationKind)
/// combination (issue #724). Before the fix, shape was decided by two separate switch
/// statements — one on <see cref="MatterType"/>, one on <see cref="PolarizationKind"/> — that
/// collided: "square" meant both TM and electrical, "circle" meant both TE and optical. This
/// pins down the single, non-colliding decision function that replaced them.
/// </summary>
public class PinGlyphSelectorTests
{
    [Theory]
    [InlineData(MatterType.Electricity, PolarizationKind.TE, PinGlyph.ElectricalPad)]
    [InlineData(MatterType.Electricity, PolarizationKind.TM, PinGlyph.ElectricalPad)]
    [InlineData(MatterType.Electricity, PolarizationKind.Both, PinGlyph.ElectricalPad)]
    [InlineData(MatterType.Light, PolarizationKind.TE, PinGlyph.OpticalCircle)]
    [InlineData(MatterType.Light, PolarizationKind.TM, PinGlyph.OpticalDiamond)]
    [InlineData(MatterType.Light, PolarizationKind.Both, PinGlyph.OpticalCircleWithDiamondOutline)]
    [InlineData(MatterType.None, PolarizationKind.TE, PinGlyph.OpticalCircle)]
    [InlineData(MatterType.None, PolarizationKind.TM, PinGlyph.OpticalDiamond)]
    [InlineData(MatterType.None, PolarizationKind.Both, PinGlyph.OpticalCircleWithDiamondOutline)]
    public void SelectGlyph_FullMatterTypeByPolarizationMatrix_ResolvesExpectedGlyph(
        MatterType matterType, PolarizationKind polarization, PinGlyph expected)
    {
        PinGlyphSelector.SelectGlyph(matterType, polarization).ShouldBe(expected);
    }

    [Fact]
    public void SelectGlyph_ElectricalPin_IgnoresPolarizationEntirely()
    {
        // The regression this issue exists for: an electrical pin (e.g. the Probe Pad) must
        // never pick up a polarization-derived shape, no matter what its (usually irrelevant,
        // default-TE) Polarization value happens to be.
        var glyphs = new[]
        {
            PinGlyphSelector.SelectGlyph(MatterType.Electricity, PolarizationKind.TE),
            PinGlyphSelector.SelectGlyph(MatterType.Electricity, PolarizationKind.TM),
            PinGlyphSelector.SelectGlyph(MatterType.Electricity, PolarizationKind.Both),
        };

        glyphs.ShouldAllBe(g => g == PinGlyph.ElectricalPad);
    }

    [Fact]
    public void SelectGlyph_OpticalTmPin_NeverResolvesToElectricalPad()
    {
        // The other half of the regression: an optical TM pin (e.g. "Adiabatic Coupler TM
        // 1550") must render distinguishably from an electrical pad — never the same glyph.
        PinGlyphSelector.SelectGlyph(MatterType.Light, PolarizationKind.TM)
            .ShouldNotBe(PinGlyph.ElectricalPad);
        PinGlyphSelector.SelectGlyph(MatterType.None, PolarizationKind.TM)
            .ShouldNotBe(PinGlyph.ElectricalPad);
    }
}

using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering;

/// <summary>
/// The visual glyph (shape) used to draw a physical pin marker. Chosen purely from the pin's
/// <see cref="MatterType"/> and <see cref="PolarizationKind"/>, so shape can never signal two
/// contradictory things at once (issue #724, where "square" meant both TM *and* electrical,
/// and "circle" meant both TE *and* optical).
/// </summary>
public enum PinGlyph
{
    /// <summary>Optical pin, TE polarization (or the historical default when unset) — solid circle.</summary>
    OpticalCircle,

    /// <summary>
    /// Optical pin, TM polarization — solid diamond. A diamond, not a square, so a TM pin can
    /// never be mistaken for the electrical pad glyph (issue #724: "Adiabatic Coupler TM 1550"
    /// used to render as a plain square — visually identical to an electrical contact).
    /// </summary>
    OpticalDiamond,

    /// <summary>Optical pin, polarization-agnostic ("Both") — circle with a diamond outline, signalling it accepts either TE or TM.</summary>
    OpticalCircleWithDiamondOutline,

    /// <summary>
    /// Electrical pin (metal contact/pad) — filled square with a contact-rim border. Chosen
    /// regardless of <see cref="PolarizationKind"/>: polarization is an optical-only concept,
    /// so it is never consulted once a pin is electrical (this was the actual root cause of the
    /// Probe Pad rendering round in #724 — its pin defaults to <see cref="PolarizationKind.TE"/>
    /// because it has no explicit polarization, and the old shape switch keyed off polarization
    /// alone, never checking <see cref="MatterType"/>).
    /// </summary>
    ElectricalPad
}

/// <summary>
/// Pure decision function for <see cref="PinGlyph"/>. Single place that resolves the
/// (MatterType × PolarizationKind) matrix to a shape, replacing the two independently-switching,
/// collided code paths that used to live in <see cref="PinRenderer"/> (issue #724).
/// </summary>
internal static class PinGlyphSelector
{
    /// <summary>
    /// Selects the glyph for a pin. Electrical pins always resolve to
    /// <see cref="PinGlyph.ElectricalPad"/>, independent of <paramref name="polarization"/>;
    /// polarization only shapes the glyph for optical pins (<see cref="MatterType.Light"/> and
    /// the legacy <see cref="MatterType.None"/> default are both treated as optical, matching
    /// <see cref="CAP_Core.Components.PinKinds.PinKindHelper.AreKindsCompatible"/>).
    /// </summary>
    public static PinGlyph SelectGlyph(MatterType matterType, PolarizationKind polarization)
    {
        if (matterType == MatterType.Electricity)
            return PinGlyph.ElectricalPad;

        return polarization switch
        {
            PolarizationKind.TM => PinGlyph.OpticalDiamond,
            PolarizationKind.Both => PinGlyph.OpticalCircleWithDiamondOutline,
            _ => PinGlyph.OpticalCircle,
        };
    }
}

namespace CAP_Core.Analysis;

/// <summary>
/// The DRC-lite rule set governing ONE waveguide connection, resolved from the
/// fabrication processes of the PDKs its endpoint pins belong to (issue #936).
/// On a multi-process canvas (e.g. a Cornerstone SiN chiplet next to a SiEPIC SOI
/// chiplet) each connection is checked against its own endpoints' process limits
/// instead of one design-wide rule set taken from the active process' first member
/// PDK. Only declared values participate: a PDK that declares no
/// <c>minWidthUm</c>/<c>minWaveguideSpacingUm</c> contributes nothing and the
/// connection stays silent for that rule kind — no invented values (the #926 rule).
/// </summary>
public sealed class ConnectionDrcRules
{
    /// <summary>
    /// Creates the per-connection rule set.
    /// </summary>
    /// <param name="widthRules">
    /// Union of both endpoint processes' per-cross-section minimum feature widths;
    /// the pin-layer association in <see cref="WaveguideMinWidthChecker"/> picks the
    /// rule of the cross-section a pin is drawn on. Empty when neither endpoint PDK
    /// declares a minimum.
    /// </param>
    /// <param name="minSpacingMicrometers">
    /// The stricter (larger) of the two endpoint processes' declared minimum
    /// edge-to-edge waveguide spacings; ≤0 when neither declares one (spacing rule
    /// silent for this connection).
    /// </param>
    public ConnectionDrcRules(
        IReadOnlyList<WaveguideMinWidthRule> widthRules,
        double minSpacingMicrometers)
    {
        WidthRules = widthRules;
        MinSpacingMicrometers = minSpacingMicrometers;
    }

    /// <summary>Per-cross-section minimum feature widths of the endpoint processes.</summary>
    public IReadOnlyList<WaveguideMinWidthRule> WidthRules { get; }

    /// <summary>Governing minimum edge-to-edge spacing in µm; ≤0 means no declared limit.</summary>
    public double MinSpacingMicrometers { get; }
}

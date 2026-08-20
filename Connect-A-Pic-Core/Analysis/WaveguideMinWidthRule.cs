namespace CAP_Core.Analysis;

/// <summary>
/// Fabrication minimum feature width of one optical cross-section of the active
/// process, resolved from the PDK's <c>minWidthUm</c> declaration. A connection is
/// associated with a rule through the GDS layer stamped on its endpoint pins
/// (see <c>PhysicalPin.Layer</c>); rules are built per design from the active
/// process, so no single-process assumption is baked in.
/// </summary>
public sealed class WaveguideMinWidthRule
{
    /// <summary>
    /// Creates a rule for one optical cross-section.
    /// </summary>
    /// <param name="minWidthMicrometers">Foundry minimum feature width in µm.</param>
    /// <param name="gdsLayers">GDS layer numbers of the cross-section (resolved via the process layer stack).</param>
    /// <param name="xsectionName">Cross-section name (e.g. "xs_nc"), for message attribution.</param>
    /// <param name="drcSource">Provenance of the value (foundry document/table), optional.</param>
    public WaveguideMinWidthRule(
        double minWidthMicrometers,
        IReadOnlyCollection<int> gdsLayers,
        string xsectionName,
        string? drcSource)
    {
        MinWidthMicrometers = minWidthMicrometers;
        GdsLayers = gdsLayers;
        XsectionName = xsectionName;
        DrcSource = drcSource;
    }

    /// <summary>Foundry minimum feature width in µm for this cross-section.</summary>
    public double MinWidthMicrometers { get; }

    /// <summary>GDS layer numbers this cross-section is drawn on.</summary>
    public IReadOnlyCollection<int> GdsLayers { get; }

    /// <summary>Cross-section name the limit belongs to.</summary>
    public string XsectionName { get; }

    /// <summary>Provenance of the limit (foundry document/table/script), when declared.</summary>
    public string? DrcSource { get; }
}

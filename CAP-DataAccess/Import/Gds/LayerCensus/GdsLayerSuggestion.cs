namespace CAP_DataAccess.Import.Gds.LayerCensus;

/// <summary>
/// The role a layer suggestion proposes for a (layer, datatype) pair — one of
/// the import dialog's three layer fields, or the explicitly undecidable
/// "routing, kind unknown" (geometry alone cannot tell optical from metal
/// routing; the user must decide which field it belongs to).
/// </summary>
public enum GdsLayerRole
{
    /// <summary>TEXT elements on the pair look like port labels.</summary>
    PortLabels,

    /// <summary>The pair looks like a waveguide-core (optical) layer.</summary>
    Waveguide,

    /// <summary>The pair looks like a metal (electrical) layer.</summary>
    Metal,

    /// <summary>
    /// The pair carries route-like strokes in the top cell, but whether they are
    /// optical or electrical is undecidable from geometry — the suggestion is
    /// deliberately marked "kind unknown" instead of guessing.
    /// </summary>
    RoutingUnknown,
}

/// <summary>How strongly the evidence backs a <see cref="GdsLayerSuggestion"/>.</summary>
public enum GdsSuggestionConfidence
{
    /// <summary>Geometry heuristic only (e.g. route-like strokes, kind unknown).</summary>
    Low,

    /// <summary>
    /// Convention guess: the layer number matches a known metal/waveguide
    /// table — but numbers collide across foundries (one foundry's M1 is
    /// another's core etch), so optical vs electrical from a bare number stays
    /// a guess the user confirms.
    /// </summary>
    Medium,

    /// <summary>
    /// Strong, auto-appliable evidence: a text-backed port-label layer (a known
    /// port convention whose layer bears texts, or single-line text labels).
    /// </summary>
    High,
}

/// <summary>
/// One visible, user-confirmable layer-assignment suggestion for the import
/// dialog: a (layer, datatype) pair, the proposed role, the confidence and a
/// human-readable reason naming the evidence. Suggestions are never applied
/// silently — the dialog renders them as chips the user accepts into the
/// layer fields.
/// </summary>
/// <param name="Layer">GDS layer number.</param>
/// <param name="Datatype">GDS datatype (texttype for text evidence).</param>
/// <param name="Role">The proposed role.</param>
/// <param name="Confidence">How strongly the evidence backs the proposal.</param>
/// <param name="Reason">Human-readable provenance ("what makes us think so").</param>
public sealed record GdsLayerSuggestion(
    int Layer,
    int Datatype,
    GdsLayerRole Role,
    GdsSuggestionConfidence Confidence,
    string Reason);

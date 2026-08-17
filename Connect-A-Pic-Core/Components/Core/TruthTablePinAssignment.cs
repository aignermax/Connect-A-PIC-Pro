namespace CAP_Core.Components.Core;

/// <summary>
/// Pin-role assignment the Truth Table panel last successfully extracted with for a
/// group (issue #981): the logic input pins in bit order, the logic output pins, the
/// bias pins that are "on" in every row, and the analog→digital power threshold.
/// Persisted in the .lun design file on top-level groups so the panel can prefill
/// after a save → load round trip without the user re-assigning roles by hand.
/// Lives in <c>Components.Core</c> (not <c>Analysis.LogicAnalysis</c>) because the
/// shared-kernel <see cref="ComponentGroup"/> carries it — the Analysis feature
/// imports Components, not the other way round.
/// </summary>
public sealed class TruthTablePinAssignment
{
    /// <summary>Logic input pin names in the truth table's bit order (column order).</summary>
    public List<string> InputPinNames { get; set; } = new();

    /// <summary>Logic output pin names.</summary>
    public List<string> OutputPinNames { get; set; } = new();

    /// <summary>Bias pin names — constantly "on" in every row (inversion-gate ingredient).</summary>
    public List<string> BiasPinNames { get; set; } = new();

    /// <summary>
    /// Normalized power threshold in the open interval (0, 1) the table was
    /// extracted at: an output counts as logic 1 at or above it.
    /// </summary>
    public double Threshold { get; set; }
}

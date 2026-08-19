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

    /// <summary>
    /// Optional network-signal name per logic input pin (issue #1025): unconnected
    /// input pins carrying the same signal name merge into one network-level input
    /// (the full adder's addends A, B and the carry-in Cin become three toggles,
    /// not thirty) and one fan-out site with its true load count. Pins without an
    /// entry keep their own <c>&lt;gate&gt;.&lt;pin&gt;</c> name — no merging, so
    /// unrelated inputs that happen to share a pin name stay separate. Null when no
    /// pin carries a signal name; legacy files without the block load unchanged.
    /// </summary>
    public Dictionary<string, string>? InputSignalNames { get; set; }

    /// <summary>
    /// Optional signal name per logic output pin: the named output's network tap
    /// carries the signal name instead of the raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id —
    /// the 4-bit adder's sum reads <c>S0</c>–<c>S3</c> and <c>Cout</c>, not
    /// <c>T0H2SUM.Y</c> … <c>T3OROUT.Y</c>. Unlike input names, output names never
    /// merge pins (every tap is one gate output), so they must be unique across the
    /// network. Null when no output pin carries a name; legacy files without the
    /// block load unchanged.
    /// </summary>
    public Dictionary<string, string>? OutputSignalNames { get; set; }

    /// <summary>
    /// Creates an independent copy of the assignment: the lists and the signal-name
    /// dictionaries are mutable, so copies of one gate must not share them.
    /// </summary>
    /// <returns>A new assignment with the same roles, threshold, and signal names.</returns>
    public TruthTablePinAssignment Copy() => new()
    {
        InputPinNames = new List<string>(InputPinNames),
        OutputPinNames = new List<string>(OutputPinNames),
        BiasPinNames = new List<string>(BiasPinNames),
        Threshold = Threshold,
        InputSignalNames = InputSignalNames == null
            ? null
            : new Dictionary<string, string>(InputSignalNames),
        OutputSignalNames = OutputSignalNames == null
            ? null
            : new Dictionary<string, string>(OutputSignalNames)
    };
}

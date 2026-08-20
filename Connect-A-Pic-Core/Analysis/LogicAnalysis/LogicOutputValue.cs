namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// One evaluated logic output of a truth-table row: the classified bit together with
/// the raw simulated power, so it stays visible <em>why</em> an output is 1 —
/// a 0.93 and a 0.51 above the same threshold teach different lessons about the gate.
/// </summary>
/// <param name="IsOne">True when <see cref="Power"/> reached the table's power threshold.</param>
/// <param name="Power">
/// Normalized optical power leaving the group through this output pin
/// (1.0 = the full power of one active input).
/// </param>
public sealed record LogicOutputValue(bool IsOne, double Power);

using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// One row of a <see cref="TruthTable"/>: one binary input combination mapped to the
/// evaluated logic outputs of the grouped circuit.
/// </summary>
public sealed class TruthTableRow
{
    /// <summary>Assembles one extraction row.</summary>
    public TruthTableRow(
        IReadOnlyDictionary<string, bool> inputBits,
        IReadOnlyDictionary<string, LogicOutputValue> outputs)
    {
        InputBits = inputBits;
        Outputs = outputs;
    }

    /// <summary>Logic level per input pin name for this combination.</summary>
    public IReadOnlyDictionary<string, bool> InputBits { get; }

    /// <summary>Evaluated logic outputs (bit plus raw power) per output pin name.</summary>
    public IReadOnlyDictionary<string, LogicOutputValue> Outputs { get; }
}

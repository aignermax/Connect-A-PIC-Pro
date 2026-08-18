using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// The digital truth table extracted from a <see cref="ComponentGroup"/> by
/// <see cref="TruthTableExtractor"/>: every binary input combination mapped to the
/// resulting logic outputs, each output carrying both its classified bit and the
/// raw simulated power behind it.
/// </summary>
public sealed class TruthTable
{
    /// <summary>Assembles an immutable extraction result without bias pins.</summary>
    public TruthTable(
        string groupName,
        IReadOnlyList<string> inputPinNames,
        IReadOnlyList<string> outputPinNames,
        double powerThreshold,
        int wavelengthNm,
        IReadOnlyList<TruthTableRow> rows)
        : this(groupName, inputPinNames, outputPinNames, [], powerThreshold, wavelengthNm, rows)
    {
    }

    /// <summary>Assembles an immutable extraction result including the bias-pin assignment.</summary>
    public TruthTable(
        string groupName,
        IReadOnlyList<string> inputPinNames,
        IReadOnlyList<string> outputPinNames,
        IReadOnlyList<string> biasPinNames,
        double powerThreshold,
        int wavelengthNm,
        IReadOnlyList<TruthTableRow> rows)
    {
        GroupName = groupName;
        InputPinNames = inputPinNames;
        OutputPinNames = outputPinNames;
        BiasPinNames = biasPinNames;
        PowerThreshold = powerThreshold;
        WavelengthNm = wavelengthNm;
        Rows = rows;
    }

    /// <summary>Name of the group the table was extracted from.</summary>
    public string GroupName { get; }

    /// <summary>Logic input pin names, in the bit order used by <see cref="Rows"/>.</summary>
    public IReadOnlyList<string> InputPinNames { get; }

    /// <summary>Logic output pin names.</summary>
    public IReadOnlyList<string> OutputPinNames { get; }

    /// <summary>Bias pin names — constantly "on" in every row, so they never appear as input-bit columns.</summary>
    public IReadOnlyList<string> BiasPinNames { get; }

    /// <summary>Normalized power threshold used for classification (power ≥ threshold is logic 1).</summary>
    public double PowerThreshold { get; }

    /// <summary>Laser wavelength in nm the table was simulated at.</summary>
    public int WavelengthNm { get; }

    /// <summary>
    /// One row per binary input combination — 2^<see cref="InputPinNames"/>.Count rows
    /// in binary counting order: bit i of the row index is the logic level of
    /// InputPinNames[i], so row 0 is all inputs off and the last row is all inputs on.
    /// </summary>
    public IReadOnlyList<TruthTableRow> Rows { get; }
}

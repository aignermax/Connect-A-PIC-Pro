namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One row of the extracted truth table: the input bit pattern plus one cell per
/// output pin.
/// </summary>
public class TruthTableRowViewModel
{
    /// <summary>Initializes the row with its bit pattern and output cells.</summary>
    public TruthTableRowViewModel(string inputBitsText, IReadOnlyList<TruthTableOutputCellViewModel> outputCells)
    {
        InputBitsText = inputBitsText;
        OutputCells = outputCells;
    }

    /// <summary>Space-separated input bits in header pin order, e.g. "0 1".</summary>
    public string InputBitsText { get; }

    /// <summary>One cell per output pin, in header pin order.</summary>
    public IReadOnlyList<TruthTableOutputCellViewModel> OutputCells { get; }
}

/// <summary>
/// One output cell of a truth table row: the classified bit and the raw simulated
/// power behind it — the education core of the panel (0.93 vs. 0.51 above the same
/// threshold are different lessons).
/// </summary>
public class TruthTableOutputCellViewModel
{
    /// <summary>Initializes the cell with the classified bit and the formatted power.</summary>
    public TruthTableOutputCellViewModel(bool isOne, string powerText)
    {
        IsOne = isOne;
        PowerText = powerText;
    }

    /// <summary>True when the output reached the threshold (logic 1).</summary>
    public bool IsOne { get; }

    /// <summary>The classified bit as display text ("1" or "0").</summary>
    public string BitText => IsOne ? "1" : "0";

    /// <summary>Raw normalized power behind the bit, formatted with two decimals.</summary>
    public string PowerText { get; }
}

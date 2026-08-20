namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Immutable logic-level model of one gate: its input pin names, its output pin
/// names, and the <see cref="Analysis.LogicAnalysis.TruthTable"/> a
/// <see cref="TruthTableExtractor"/> produced for the grouped photonic circuit.
/// Evaluating the model is a pure table lookup — no re-simulation — and every
/// output is a clean bit, so a network of these models restores ideal logic
/// levels at every stage by construction.
/// </summary>
public sealed class LogicGateModel
{
    private LogicGateModel(TruthTable table)
    {
        TruthTable = table;
        GateName = table.GroupName;
        InputPinNames = table.InputPinNames;
        OutputPinNames = table.OutputPinNames;
    }

    /// <summary>Name of the gate — the group the truth table was extracted from.</summary>
    public string GateName { get; }

    /// <summary>Logic input pin names, in the bit order of the truth table rows.</summary>
    public IReadOnlyList<string> InputPinNames { get; }

    /// <summary>Logic output pin names.</summary>
    public IReadOnlyList<string> OutputPinNames { get; }

    /// <summary>The extracted truth table backing this model, raw powers included.</summary>
    public TruthTable TruthTable { get; }

    /// <summary>
    /// Wraps an extracted truth table as an evaluable gate model, verifying the
    /// table is complete and self-consistent: exactly 2^input rows in binary
    /// counting order, every row carrying exactly the declared input and output pins.
    /// </summary>
    /// <param name="table">The truth table as produced by <see cref="TruthTableExtractor"/>.</param>
    /// <returns>The immutable gate model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    /// <exception cref="ArgumentException">The table is incomplete or inconsistent.</exception>
    public static LogicGateModel FromTruthTable(TruthTable table)
    {
        if (table == null) throw new ArgumentNullException(nameof(table));
        ValidatePinNames(table);
        ValidateRows(table);
        return new LogicGateModel(table);
    }

    /// <summary>
    /// Looks up the gate's output bits for one input combination — a pure table
    /// read, so the results are clean bits with ideal level restoration.
    /// </summary>
    /// <param name="inputBits">One bit per declared input pin name.</param>
    /// <returns>The output bit per declared output pin name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputBits"/> is null.</exception>
    /// <exception cref="ArgumentException">A declared input bit is missing or an unknown pin is passed.</exception>
    public IReadOnlyDictionary<string, bool> Evaluate(IReadOnlyDictionary<string, bool> inputBits)
    {
        if (inputBits == null) throw new ArgumentNullException(nameof(inputBits));

        var pattern = 0;
        for (var i = 0; i < InputPinNames.Count; i++)
        {
            if (!inputBits.TryGetValue(InputPinNames[i], out var bit))
                throw new ArgumentException(
                    $"Gate '{GateName}' is missing a bit for input pin '{InputPinNames[i]}'. " +
                    $"Required inputs: {string.Join(", ", InputPinNames)}.",
                    nameof(inputBits));
            if (bit) pattern |= 1 << i;
        }

        var unknown = inputBits.Keys.Except(InputPinNames).FirstOrDefault();
        if (unknown != null)
            throw new ArgumentException(
                $"Gate '{GateName}' has no input pin named '{unknown}'. " +
                $"Available inputs: {string.Join(", ", InputPinNames)}.",
                nameof(inputBits));

        return TruthTable.Rows[pattern].Outputs.ToDictionary(
            pair => pair.Key, pair => pair.Value.IsOne);
    }

    /// <summary>Rejects empty or duplicated pin declarations before any row is inspected.</summary>
    private static void ValidatePinNames(TruthTable table)
    {
        if (table.InputPinNames.Count == 0)
            throw new ArgumentException(
                $"Truth table of group '{table.GroupName}' declares no input pins.", nameof(table));
        if (table.OutputPinNames.Count == 0)
            throw new ArgumentException(
                $"Truth table of group '{table.GroupName}' declares no output pins.", nameof(table));

        var duplicate = table.InputPinNames.Concat(table.OutputPinNames)
            .GroupBy(name => name).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException(
                $"Truth table of group '{table.GroupName}' declares pin '{duplicate}' more than once.",
                nameof(table));
    }

    /// <summary>
    /// Verifies the row count matches 2^inputs and that every row sits at its
    /// binary-counting index with exactly the declared pins — the ordering
    /// contract <see cref="Evaluate"/> relies on for its direct index lookup.
    /// </summary>
    private static void ValidateRows(TruthTable table)
    {
        var expectedRows = 1 << table.InputPinNames.Count;
        if (table.Rows.Count != expectedRows)
            throw new ArgumentException(
                $"Truth table of group '{table.GroupName}' has {table.Rows.Count} rows but " +
                $"{table.InputPinNames.Count} inputs require exactly {expectedRows}.",
                nameof(table));

        for (var pattern = 0; pattern < expectedRows; pattern++)
        {
            var row = table.Rows[pattern];
            if (row.InputBits.Count != table.InputPinNames.Count)
                throw new ArgumentException(
                    $"Row {pattern} of group '{table.GroupName}' carries {row.InputBits.Count} input bits " +
                    $"but the table declares {table.InputPinNames.Count} input pins.",
                    nameof(table));
            for (var i = 0; i < table.InputPinNames.Count; i++)
            {
                var pin = table.InputPinNames[i];
                if (!row.InputBits.TryGetValue(pin, out var bit) || bit != ((pattern & (1 << i)) != 0))
                    throw new ArgumentException(
                        $"Row {pattern} of group '{table.GroupName}' does not carry input pin '{pin}' " +
                        "at its binary-counting level — rows must be in binary counting order.",
                        nameof(table));
            }

            var missingOutput = table.OutputPinNames.FirstOrDefault(pin => !row.Outputs.ContainsKey(pin));
            if (missingOutput != null)
                throw new ArgumentException(
                    $"Row {pattern} of group '{table.GroupName}' carries no value for output pin '{missingOutput}'.",
                    nameof(table));
        }
    }
}

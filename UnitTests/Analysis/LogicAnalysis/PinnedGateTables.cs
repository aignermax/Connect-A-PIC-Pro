using CAP_Core.Analysis.LogicAnalysis;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The pinned NAND/NOT truth tables of the shipped <c>Logic Gate NOT-NAND.lun</c>
/// example, rebuilt by value: extracted at 1550 nm with the BIAS pin constantly
/// on, NAND at threshold 0.125 reads 1/1/1/0 with raw powers 0.5/0.25/0.25/0.0,
/// NOT at threshold 0.375 reads 1/0 with raw powers 0.5/0.25. Logic-layer unit
/// tests use these value-built copies to stay fast and deterministic; the
/// integration tests re-derive the same tables from the real simulation.
/// </summary>
internal static class PinnedGateTables
{
    /// <summary>Normalized power threshold at which the example reads NAND.</summary>
    public const double NandThreshold = 0.125;

    /// <summary>Normalized power threshold at which the example reads NOT.</summary>
    public const double NotThreshold = 0.375;

    /// <summary>Laser wavelength the pinned tables were extracted at.</summary>
    public const int WavelengthNm = 1550;

    private const string GroupName = "NOT/NAND Gate";
    private const double RestingPower = 0.5;
    private const double SingleInputPower = 0.25;

    private static readonly string[] NandInputs = { "A", "B" };
    private static readonly string[] NotInputs = { "A" };
    private static readonly string[] Outputs = { "Y" };
    private static readonly string[] Biases = { "BIAS" };

    /// <summary>The pinned NAND table: rows in binary counting order, bit 0 = A.</summary>
    public static TruthTable Nand() =>
        new(
            GroupName,
            NandInputs,
            Outputs,
            Biases,
            NandThreshold,
            WavelengthNm,
            new[]
            {
                Row(NandInputs, new[] { false, false }, true, RestingPower),
                Row(NandInputs, new[] { true, false }, true, SingleInputPower),
                Row(NandInputs, new[] { false, true }, true, SingleInputPower),
                Row(NandInputs, new[] { true, true }, false, 0.0),
            });

    /// <summary>The pinned NOT table: row 0 = input off, row 1 = input on.</summary>
    public static TruthTable Not() =>
        new(
            GroupName,
            NotInputs,
            Outputs,
            Biases,
            NotThreshold,
            WavelengthNm,
            new[]
            {
                Row(NotInputs, new[] { false }, true, RestingPower),
                Row(NotInputs, new[] { true }, false, SingleInputPower),
            });

    /// <summary>The pinned NAND table wrapped as an evaluable gate model.</summary>
    public static LogicGateModel NandGate() => LogicGateModel.FromTruthTable(Nand());

    /// <summary>The pinned NOT table wrapped as an evaluable gate model.</summary>
    public static LogicGateModel NotGate() => LogicGateModel.FromTruthTable(Not());

    /// <summary>Builds one table row: the input bits per pin name plus the single Y output.</summary>
    private static TruthTableRow Row(string[] inputNames, bool[] bits, bool y, double power) =>
        new(
            inputNames.Select((name, i) => (name, bit: bits[i])).ToDictionary(pair => pair.name, pair => pair.bit),
            new Dictionary<string, LogicOutputValue> { ["Y"] = new(y, power) });
}

using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The logic-level gate model: a truth table wrapped as an evaluable gate whose
/// outputs are clean bits by construction. Covers the pinned NAND table, the
/// table-consistency validation, and the input-bit error paths.
/// </summary>
public class LogicGateModelTests
{
    [Fact]
    public void FromTruthTable_PinnedNandTable_ExposesTheGateInterface()
    {
        var table = PinnedGateTables.Nand();

        var gate = LogicGateModel.FromTruthTable(table);

        gate.GateName.ShouldBe("NOT/NAND Gate");
        gate.InputPinNames.ShouldBe(new[] { "A", "B" });
        gate.OutputPinNames.ShouldBe(new[] { "Y" });
        gate.TruthTable.ShouldBeSameAs(table);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Evaluate_PinnedNandGate_ReturnsCleanBitsForEveryCombination(bool a, bool b, bool expected)
    {
        var gate = PinnedGateTables.NandGate();

        var outputs = gate.Evaluate(new Dictionary<string, bool> { ["A"] = a, ["B"] = b });

        outputs.ShouldBe(new Dictionary<string, bool> { ["Y"] = expected });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Evaluate_PinnedNotGate_InvertsTheInput(bool a, bool expected)
    {
        var gate = PinnedGateTables.NotGate();

        gate.Evaluate(new Dictionary<string, bool> { ["A"] = a })["Y"].ShouldBe(expected);
    }

    [Fact]
    public void FromTruthTable_NullTable_Throws()
    {
        Should.Throw<ArgumentNullException>(() => LogicGateModel.FromTruthTable(null!));
    }

    [Fact]
    public void FromTruthTable_RowCountMismatch_Throws()
    {
        var table = PinnedGateTables.Nand();
        var broken = new TruthTable(
            table.GroupName, table.InputPinNames, table.OutputPinNames, table.BiasPinNames,
            table.PowerThreshold, table.WavelengthNm, table.Rows.Take(3).ToArray());

        var error = Should.Throw<ArgumentException>(() => LogicGateModel.FromTruthTable(broken));

        error.Message.ShouldContain("3 rows");
        error.Message.ShouldContain("require exactly 4");
    }

    [Fact]
    public void FromTruthTable_RowOutOfBinaryCountingOrder_Throws()
    {
        var table = PinnedGateTables.Nand();
        var swapped = new[] { table.Rows[0], table.Rows[2], table.Rows[1], table.Rows[3] };
        var broken = new TruthTable(
            table.GroupName, table.InputPinNames, table.OutputPinNames, table.BiasPinNames,
            table.PowerThreshold, table.WavelengthNm, swapped);

        var error = Should.Throw<ArgumentException>(() => LogicGateModel.FromTruthTable(broken));

        error.Message.ShouldContain("binary counting order");
    }

    [Fact]
    public void FromTruthTable_RowMissingAnOutput_Throws()
    {
        var table = PinnedGateTables.Nand();
        var rowWithoutOutput = new TruthTableRow(
            table.Rows[0].InputBits, new Dictionary<string, LogicOutputValue>());
        var brokenRows = new[] { rowWithoutOutput, table.Rows[1], table.Rows[2], table.Rows[3] };
        var broken = new TruthTable(
            table.GroupName, table.InputPinNames, table.OutputPinNames, table.BiasPinNames,
            table.PowerThreshold, table.WavelengthNm, brokenRows);

        var error = Should.Throw<ArgumentException>(() => LogicGateModel.FromTruthTable(broken));

        error.Message.ShouldContain("output pin 'Y'");
    }

    [Fact]
    public void FromTruthTable_DuplicatePinDeclaration_Throws()
    {
        var table = PinnedGateTables.Not();
        var broken = new TruthTable(
            table.GroupName, new[] { "A", "A" }, table.OutputPinNames, table.BiasPinNames,
            table.PowerThreshold, table.WavelengthNm, table.Rows);

        var error = Should.Throw<ArgumentException>(() => LogicGateModel.FromTruthTable(broken));

        error.Message.ShouldContain("'A' more than once");
    }

    [Fact]
    public void Evaluate_MissingInputBit_ThrowsNamingThePin()
    {
        var gate = PinnedGateTables.NandGate();

        var error = Should.Throw<ArgumentException>(
            () => gate.Evaluate(new Dictionary<string, bool> { ["A"] = true }));

        error.Message.ShouldContain("'B'");
        error.Message.ShouldContain("Required inputs: A, B");
    }

    [Fact]
    public void Evaluate_UnknownInputPin_ThrowsNamingThePin()
    {
        var gate = PinnedGateTables.NandGate();

        var error = Should.Throw<ArgumentException>(
            () => gate.Evaluate(new Dictionary<string, bool> { ["A"] = true, ["B"] = false, ["C"] = true }));

        error.Message.ShouldContain("no input pin named 'C'");
    }
}

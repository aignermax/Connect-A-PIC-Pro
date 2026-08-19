using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for the Logic panel's bus view (issue #1068, NAND game rung 5): name-family
/// detection groups <c>A0</c>–<c>A3</c> into bus <c>A</c> and ignores <c>Cin</c>, the
/// decimal value derives from the member bits LSB-first, and typing a decimal into an
/// input bus writes the member toggles with out-of-range input clamped.
/// </summary>
public class SignalBusGroupingTests
{
    [Theory]
    [InlineData("A0", "A", 0)]
    [InlineData("A3", "A", 3)]
    [InlineData("S12", "S", 12)]
    [InlineData("Sel_2", "Sel_", 2)]
    public void TrySplit_IndexedName_YieldsPrefixAndIndex(string name, string expectedPrefix, int expectedIndex)
    {
        SignalBusName.TrySplit(name, out var prefix, out var index).ShouldBeTrue();

        prefix.ShouldBe(expectedPrefix);
        index.ShouldBe(expectedIndex);
    }

    [Theory]
    [InlineData("Cin")] // no trailing index
    [InlineData("Cout")]
    [InlineData("NAND1A.A")] // raw gate.pin id
    [InlineData("T3OROUT.Y")]
    [InlineData("A")] // no index
    [InlineData("12")] // empty prefix
    [InlineData("A99999999999999999999")] // index overflows int
    [InlineData("A63")] // above the 63-bit bus width
    public void TrySplit_NonBusName_ReturnsFalse(string name)
    {
        SignalBusName.TrySplit(name, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void GroupInputs_IndexedFamily_CollapsesIntoBusRow_LeavesSinglesAlone()
    {
        var inputs = new[]
        {
            new LogicNetworkInputViewModel("Cin"),
            new LogicNetworkInputViewModel("A0"),
            new LogicNetworkInputViewModel("A1"),
            new LogicNetworkInputViewModel("A2"),
            new LogicNetworkInputViewModel("A3"),
            new LogicNetworkInputViewModel("Sel0"), // lone indexed name: no family
        };

        var rows = SignalBusGrouping.GroupInputs(inputs);

        rows.Count.ShouldBe(3);
        rows[0].ShouldBeOfType<LogicNetworkInputViewModel>().PinName.ShouldBe("Cin");
        var bus = rows[1].ShouldBeOfType<LogicSignalBusInputViewModel>();
        bus.Prefix.ShouldBe("A");
        bus.Members.Select(m => m.PinName).ShouldBe(new[] { "A0", "A1", "A2", "A3" },
            "bus members list in index order, least-significant bit first");
        rows[2].ShouldBeOfType<LogicNetworkInputViewModel>().PinName.ShouldBe("Sel0");
    }

    [Fact]
    public void InputBus_DecimalValue_DerivesFromMemberBitsLeastSignificantFirst()
    {
        var inputs = new[] { "A0", "A1", "A2", "A3" }.Select(n => new LogicNetworkInputViewModel(n)).ToList();
        var bus = new LogicSignalBusInputViewModel("A", inputs);

        bus.DecimalValue.ShouldBe(0);
        bus.HeaderText.ShouldBe("A = 0 (0000)");

        bus.Members[0].IsOn = true; // A0 = LSB
        bus.Members[1].IsOn = true; // A1

        bus.DecimalValue.ShouldBe(3);
        bus.HeaderText.ShouldBe("A = 3 (0011)");
    }

    [Fact]
    public void InputBus_QuickSetDecimal_WritesMemberTogglesAndClampsOutOfRange()
    {
        var inputs = new[] { "A0", "A1", "A2", "A3" }.Select(n => new LogicNetworkInputViewModel(n)).ToList();
        var bus = new LogicSignalBusInputViewModel("A", inputs);

        bus.ValueText = "5";

        bus.Members.Select(m => m.IsOn).ShouldBe(new[] { true, false, true, false },
            "5 = 0101 with index 0 as the least-significant bit");
        bus.ValueText.ShouldBe("5", "the field canonicalizes to the applied value");
        bus.HeaderText.ShouldBe("A = 5 (0101)");

        bus.ValueText = "99"; // beyond the 4-bit maximum of 15

        bus.DecimalValue.ShouldBe(15, "out-of-range input clamps to the bus maximum");
        bus.Members.ShouldAllBe(m => m.IsOn);
        bus.ValueText.ShouldBe("15", "the field shows the clamped value, not the raw input");

        bus.ValueText = "not a number";

        bus.DecimalValue.ShouldBe(15, "unparseable input leaves the bits untouched");
        bus.ValueText.ShouldBe("15");
    }

    [Fact]
    public void OutputBus_DecimalValue_FollowsTheLiveMemberBits()
    {
        var outputs = new[] { "S0", "S1", "S2", "S3" }.Select(n => new LogicNetworkOutputViewModel(n)).ToList();
        var bus = new LogicSignalBusOutputViewModel("S", outputs);

        bus.DecimalValue.ShouldBe(0);

        bus.Members[1].IsOne = true; // S1
        bus.Members[3].IsOne = true; // S3

        bus.DecimalValue.ShouldBe(10, "S1 + S3 set = 2 + 8 (index 0 = LSB)");
        bus.HeaderText.ShouldBe("S = 10 (1010)");
    }

    [Fact]
    public void GroupOutputs_NamedFamilyCollapses_RawTapNamesStayPlain()
    {
        var outputs = new[]
        {
            new LogicNetworkOutputViewModel("S0"),
            new LogicNetworkOutputViewModel("S1"),
            new LogicNetworkOutputViewModel("Cout"),
            new LogicNetworkOutputViewModel("T3OROUT.Y"),
        };

        var rows = SignalBusGrouping.GroupOutputs(outputs);

        rows.Count.ShouldBe(3);
        rows[0].ShouldBeOfType<LogicSignalBusOutputViewModel>().Prefix.ShouldBe("S");
        rows[1].ShouldBeOfType<LogicNetworkOutputViewModel>().PinName.ShouldBe("Cout");
        rows[2].ShouldBeOfType<LogicNetworkOutputViewModel>().PinName.ShouldBe("T3OROUT.Y");
    }
}

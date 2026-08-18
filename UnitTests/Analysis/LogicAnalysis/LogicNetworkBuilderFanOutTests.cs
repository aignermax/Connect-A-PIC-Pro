using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Optical fan-out detection at the builder level (issue #996, rung 4 education
/// honesty): a gate output wired to more than one gate input, and a network-input
/// signal shared by several unconnected gate inputs that carry the same pin name,
/// each surface a structured <see cref="LogicFanOutWarning"/> naming the driver
/// and the load count — the logic layer restores ideal levels, but optically the
/// driver would have to be split (~3 dB per branch). A purely point-to-point
/// design produces no warnings.
/// </summary>
public class LogicNetworkBuilderFanOutTests
{
    [Fact]
    public void Build_GateOutputDrivesTwoInputs_ProducesFanOutWarningNamingPinAndLoadCount()
    {
        var source = NotInstance("SRC");
        var nand = NandInstance("NAND");
        var connections = new[] { Connect(source, "Y", nand, "A"), Connect(source, "Y", nand, "B") };

        var network = new LogicNetworkBuilder().Build(new[] { source, nand }, connections);

        var warning = network.FanOutWarnings.ShouldHaveSingleItem();
        warning.IsNetworkInputSignal.ShouldBeFalse();
        warning.DriverDisplayName.ShouldBe("SRC.Y");
        warning.LoadCount.ShouldBe(2);
        warning.LoadNames.ShouldBe(new[] { "NAND.A", "NAND.B" }, ignoreOrder: true);
    }

    [Fact]
    public void Build_GateOutputDrivesThreeInputsAcrossTwoGates_ReportsAllThreeLoads()
    {
        var source = NotInstance("SRC");
        var first = NotInstance("INV1");
        var second = NandInstance("NAND");
        var connections = new[]
        {
            Connect(source, "Y", first, "A"),
            Connect(source, "Y", second, "A"),
            Connect(source, "Y", second, "B"),
        };

        var network = new LogicNetworkBuilder().Build(new[] { source, first, second }, connections);

        var warning = network.FanOutWarnings.ShouldHaveSingleItem();
        warning.DriverDisplayName.ShouldBe("SRC.Y");
        warning.LoadCount.ShouldBe(3);
        warning.LoadNames.ShouldBe(new[] { "INV1.A", "NAND.A", "NAND.B" }, ignoreOrder: true);
    }

    [Fact]
    public void Build_TwoUnconnectedGateInputsSharingPinName_ProducesNetworkInputSignalWarning()
    {
        // Two gates whose inputs stay unconnected: each becomes a network input,
        // but both are named "*.A" — one logical signal the user has to drive
        // from a single source, which would have to be split optically.
        var first = NotInstance("INV1");
        var second = NotInstance("INV2");

        var network = new LogicNetworkBuilder().Build(
            new[] { first, second }, Array.Empty<WaveguideConnection>());

        var warning = network.FanOutWarnings.ShouldHaveSingleItem();
        warning.IsNetworkInputSignal.ShouldBeTrue();
        warning.DriverDisplayName.ShouldBe("A");
        warning.LoadCount.ShouldBe(2);
        warning.LoadNames.ShouldBe(new[] { "INV1.A", "INV2.A" }, ignoreOrder: true);
    }

    [Fact]
    public void Build_HalfAdderShapedInputFanOut_ReportsBothAddendSignals()
    {
        // The half-adder pattern (#986): two NANDs whose inputs stay unconnected
        // carry pin names A and B — addends A and B each fan out at the logic layer.
        var first = NandInstance("NAND1");
        var second = NandInstance("NAND2");

        var network = new LogicNetworkBuilder().Build(
            new[] { first, second }, Array.Empty<WaveguideConnection>());

        network.FanOutWarnings.Count.ShouldBe(2);
        var signalA = network.FanOutWarnings.Single(w => w.DriverDisplayName == "A");
        var signalB = network.FanOutWarnings.Single(w => w.DriverDisplayName == "B");
        signalA.IsNetworkInputSignal.ShouldBeTrue();
        signalB.IsNetworkInputSignal.ShouldBeTrue();
        signalA.LoadCount.ShouldBe(2);
        signalB.LoadCount.ShouldBe(2);
    }

    [Fact]
    public void Build_PointToPointChain_ProducesNoFanOutWarnings()
    {
        // NOT → NOT → NOT: every wire is one output to one input, every unconnected
        // gate input carries a unique pin name — nothing fans out.
        var first = NotInstance("INV1");
        var second = NotInstance("INV2");
        var third = NotInstance("INV3");
        var connections = new[]
        {
            Connect(first, "Y", second, "A"),
            Connect(second, "Y", third, "A"),
        };

        var network = new LogicNetworkBuilder().Build(new[] { first, second, third }, connections);

        network.FanOutWarnings.ShouldBeEmpty();
    }

    [Fact]
    public void Build_SingleGateWithUniquePinNames_ProducesNoFanOutWarnings()
    {
        var nand = NandInstance("NAND");

        var network = new LogicNetworkBuilder().Build(
            new[] { nand }, Array.Empty<WaveguideConnection>());

        network.FanOutWarnings.ShouldBeEmpty(
            "pin names A and B are unique within the network — no signal fans out");
    }

    /// <summary>A NAND gate instance on a synthetic group exposing the example's pin interface.</summary>
    private static LogicGateInstance NandInstance(string groupName) =>
        new(
            CreateGateGroup(groupName, "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold));

    /// <summary>A NOT gate instance on a synthetic group exposing the example's pin interface.</summary>
    private static LogicGateInstance NotInstance(string groupName) =>
        new(
            CreateGateGroup(groupName, "A", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold));

    /// <summary>A bare group whose external pins are connectable like canvas-synced group pins.</summary>
    private static ComponentGroup CreateGateGroup(string groupName, params string[] externalPinNames)
    {
        var group = new ComponentGroup(groupName);
        foreach (var pinName in externalPinNames)
        {
            var physicalPin = new PhysicalPin { Name = pinName, ParentComponent = group };
            group.PhysicalPins.Add(physicalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = physicalPin });
        }
        return group;
    }

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(LogicGateInstance from, string fromPin, LogicGateInstance to, string toPin) =>
        new() { StartPin = Pin(from.Group, fromPin), EndPin = Pin(to.Group, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);
}

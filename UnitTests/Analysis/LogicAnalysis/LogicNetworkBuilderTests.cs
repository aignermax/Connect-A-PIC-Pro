using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The canvas-driven network builder: synthetic gate groups wired through design
/// connections become a <see cref="LogicNetworkEvaluator"/> — linear chains, fan-out,
/// and unconnected inputs turning into network-level inputs named <c>group.pin</c> —
/// while logically invalid wirings (input–input, double-driven input, bias targets)
/// are rejected with messages naming the pins.
/// </summary>
public class LogicNetworkBuilderTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Build_NandFeedingNot_DerivesTheAndNetworkFromTheCanvasConnection(bool a, bool b, bool expected)
    {
        var nand = NandInstance("NAND");
        var inv = NotInstance("INV");
        var connections = new[] { Connect(nand, "Y", inv, "A") };

        var network = new LogicNetworkBuilder().Build(new[] { nand, inv }, connections);

        network.InputPinNames.ShouldBe(new[] { "NAND.A", "NAND.B" });
        network.OutputPinNames.ShouldBe(new[] { "NAND.Y", "INV.Y" },
            "every gate output pin becomes a network-level output tap, also when it drives another gate");
        network.Evaluate(Bits(("NAND.A", a), ("NAND.B", b)))["INV.Y"].ShouldBe(expected);
        network.Evaluate(Bits(("NAND.A", a), ("NAND.B", b)))["NAND.Y"].ShouldBe(!(a && b),
            "the intermediate gate output stays readable as a tap");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Build_ConnectionBoundToInternalPinsBehindExternalPins_ResolvesTheWire(bool a, bool b, bool expected)
    {
        // The load path binds wire endpoints to the internal component pin behind the
        // external pin, not to the group's own pin (FileOperationsViewModel.ResolvePin).
        var nand = new LogicGateInstance(
            CreateLoadBoundGateGroup("NAND", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold));
        var inv = new LogicGateInstance(
            CreateLoadBoundGateGroup("INV", "A", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold));
        var connections = new[]
        {
            new WaveguideConnection
            {
                StartPin = nand.Group.ExternalPins.Single(p => p.Name == "Y").InternalPin,
                EndPin = inv.Group.ExternalPins.Single(p => p.Name == "A").InternalPin,
            },
        };

        var network = new LogicNetworkBuilder().Build(new[] { nand, inv }, connections);

        network.InputPinNames.ShouldBe(new[] { "NAND.A", "NAND.B" });
        network.Evaluate(Bits(("NAND.A", a), ("NAND.B", b)))["INV.Y"].ShouldBe(expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Build_OutputFannedOutToTwoInputs_DrivesBothLoads(bool a, bool expected)
    {
        var source = NotInstance("SRC");
        var nand = NandInstance("NAND");
        var connections = new[] { Connect(source, "Y", nand, "A"), Connect(source, "Y", nand, "B") };

        var network = new LogicNetworkBuilder().Build(new[] { source, nand }, connections);

        network.InputPinNames.ShouldBe(new[] { "SRC.A" });
        network.Evaluate(Bits(("SRC.A", a)))["NAND.Y"].ShouldBe(expected,
            "NAND(!a, !a) restores the input bit");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Build_UnconnectedGateInput_BecomesANetworkInputNamedGroupDotPin(bool a, bool expected)
    {
        var inv = NotInstance("INV");

        var network = new LogicNetworkBuilder().Build(new[] { inv }, Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "INV.A" });
        network.OutputPinNames.ShouldBe(new[] { "INV.Y" });
        network.Evaluate(Bits(("INV.A", a))).ShouldBe(Bits(("INV.Y", expected)));
    }

    [Fact]
    public void Build_ConnectionBetweenTwoInputPins_ThrowsNamingThePins()
    {
        var nand = NandInstance("NAND");
        var inv = NotInstance("INV");
        var connections = new[] { Connect(nand, "A", inv, "A") };

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { nand, inv }, connections));

        error.Message.ShouldContain("two gate input pins");
        error.Message.ShouldContain("NAND.A");
        error.Message.ShouldContain("INV.A");
    }

    [Fact]
    public void Build_InputDrivenByTwoDifferentOutputs_ThrowsNamingThePins()
    {
        var first = NotInstance("NOT1");
        var second = NotInstance("NOT2");
        var nand = NandInstance("NAND");
        var connections = new[] { Connect(first, "Y", nand, "A"), Connect(second, "Y", nand, "A") };

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { first, second, nand }, connections));

        error.Message.ShouldContain("NAND.A");
        error.Message.ShouldContain("NOT1.Y");
        error.Message.ShouldContain("NOT2.Y");
    }

    [Fact]
    public void Build_ConnectionIntoBiasPin_ThrowsNamingThePins()
    {
        var inv = NotInstance("INV");
        var nand = NandInstance("NAND");
        var connections = new[] { Connect(inv, "Y", nand, "BIAS") };

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { inv, nand }, connections));

        error.Message.ShouldContain("bias pin");
        error.Message.ShouldContain("NAND.BIAS");
        error.Message.ShouldContain("constantly on");
    }

    [Fact]
    public void Build_ConnectionBetweenTwoOutputPins_ThrowsNamingThePins()
    {
        var nand = NandInstance("NAND");
        var inv = NotInstance("INV");
        var connections = new[] { Connect(nand, "Y", inv, "Y") };

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { nand, inv }, connections));

        error.Message.ShouldContain("two gate output pins");
        error.Message.ShouldContain("NAND.Y");
        error.Message.ShouldContain("INV.Y");
    }

    [Fact]
    public void Build_DuplicateGroupNames_ThrowsNamingTheDuplicate()
    {
        var first = NotInstance("INV");
        var second = NotInstance("INV");

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { first, second }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'INV'");
        error.Message.ShouldContain("must be unique");
    }

    [Fact]
    public void Build_RoleAssignmentMismatchingTheModel_ThrowsNamingTheGate()
    {
        var inv = new LogicGateInstance(
            CreateGateGroup("INV", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { inv }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("INV");
        error.Message.ShouldContain("input pins [A, B]");
        error.Message.ShouldContain("declares [A]");
    }

    [Fact]
    public void Build_ExternalPinWithoutRole_StaysOutOfTheLogicNetwork()
    {
        // The NOT reading of the NOT/NAND example: the same physical group, pin B unused.
        var inv = new LogicGateInstance(
            CreateGateGroup("INV", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold));

        var network = new LogicNetworkBuilder().Build(new[] { inv }, Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "INV.A" });
        network.OutputPinNames.ShouldBe(new[] { "INV.Y" });
    }

    [Fact]
    public void Build_RolePinTheGroupDoesNotExpose_ThrowsNamingThePin()
    {
        var inv = new LogicGateInstance(
            CreateGateGroup("INV", "A", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { inv }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'BIAS'");
        error.Message.ShouldContain("does not expose");
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

    /// <summary>
    /// A group shaped the way the load path delivers it: the external pins point at
    /// physical pins of a child component, so wire endpoints bind to those internal pins.
    /// </summary>
    private static ComponentGroup CreateLoadBoundGateGroup(string groupName, params string[] externalPinNames)
    {
        var group = new ComponentGroup(groupName);
        var child = TestComponentFactory.CreateStraightWaveGuide();
        group.AddChild(child);
        foreach (var pinName in externalPinNames)
        {
            var internalPin = new PhysicalPin { Name = pinName, ParentComponent = child };
            child.PhysicalPins.Add(internalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = internalPin });
        }
        return group;
    }

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(LogicGateInstance from, string fromPin, LogicGateInstance to, string toPin) =>
        new() { StartPin = Pin(from.Group, fromPin), EndPin = Pin(to.Group, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}

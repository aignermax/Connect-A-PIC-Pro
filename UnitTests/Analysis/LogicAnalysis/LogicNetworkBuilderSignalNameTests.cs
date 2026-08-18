using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Explicit signal identity for network inputs (issue #1025): unconnected gate
/// input pins carrying the same persisted signal name merge into one network-level
/// input — the full adder's thirteen addend-A pins become the single toggle
/// <c>A</c> — and evaluation drives every member pin from that one bit. A pin
/// without a signal name keeps its own <c>&lt;gate&gt;.&lt;pin&gt;</c> name and
/// never merges by bare pin name, so two unrelated inputs that happen to share a
/// pin name stay two inputs. Signal names on non-input pins and empty names are
/// rejected at build time.
/// </summary>
public class LogicNetworkBuilderSignalNameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_TwoPinsAssignedOneSignal_MergeIntoOneNetworkInputDrivingBoth(bool bit)
    {
        var first = NotInstance("INV1", signalOfA: "S");
        var second = NotInstance("INV2", signalOfA: "S");

        var network = new LogicNetworkBuilder().Build(
            new[] { first, second }, Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "S" },
            "both pins carry signal S — one network input, not two");
        var outputs = network.Evaluate(new Dictionary<string, bool> { ["S"] = bit });
        outputs["INV1.Y"].ShouldBe(!bit);
        outputs["INV2.Y"].ShouldBe(!bit, "the one signal bit drives every member pin");
    }

    [Fact]
    public void Build_FullAdderShapedSignals_AndCinStaySeparateDespiteSharedPinName()
    {
        // The full-adder shape behind the #1018 defect: the addend-A pins and the
        // carry-in pins all carry the bare pin name "A" on their gates, but signal
        // "A" and signal "Cin" are different sources — explicit signal names keep
        // them separate where the bare pin name merged them into one site.
        var addendFirst = NandInstance("H1N1A", signalOfA: "A", signalOfB: "B");
        var addendSecond = NandInstance("H1N1B", signalOfA: "A", signalOfB: "B");
        var carryFirst = NotInstance("H2N1A", signalOfA: "Cin");
        var carrySecond = NotInstance("H2N1B", signalOfA: "Cin");

        var network = new LogicNetworkBuilder().Build(
            new[] { addendFirst, addendSecond, carryFirst, carrySecond },
            Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "A", "B", "Cin" }, ignoreOrder: true);
        network.FanOutWarnings.Count.ShouldBe(3,
            "one site per signal — A and Cin never merge into one fictitious site");
        network.FanOutWarnings.ShouldAllBe(w => w.IsNetworkInputSignal);
        foreach (var (signal, loads) in new[]
                 {
                     ("A", new[] { "H1N1A.A", "H1N1B.A" }),
                     ("B", new[] { "H1N1A.B", "H1N1B.B" }),
                     ("Cin", new[] { "H2N1A.A", "H2N1B.A" }),
                 })
        {
            var warning = network.FanOutWarnings.Single(w => w.DriverDisplayName == signal);
            warning.LoadCount.ShouldBe(loads.Length);
            warning.LoadNames.ShouldBe(loads, ignoreOrder: true);
        }
    }

    [Fact]
    public void Build_PinWithoutSignalNameAlongsideSignalPins_KeepsItsGateDotPinName()
    {
        var named = NotInstance("INV1", signalOfA: "S");
        var unnamed = NotInstance("INV2");

        var network = new LogicNetworkBuilder().Build(
            new[] { named, unnamed }, Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "S", "INV2.A" }, ignoreOrder: true);
        network.FanOutWarnings.ShouldBeEmpty(
            "the unmerged pin is its own network input — no shared source");
    }

    [Fact]
    public void Build_TwoPinsOfOneGateAssignedOneSignal_MergeIntoOneNetworkInput()
    {
        // A NAND with its inputs tied together reads as NOT — one signal drives both.
        var tied = NandInstance("TIED", signalOfA: "X", signalOfB: "X");

        var network = new LogicNetworkBuilder().Build(
            new[] { tied }, Array.Empty<WaveguideConnection>());

        network.InputPinNames.ShouldBe(new[] { "X" });
        network.Evaluate(new Dictionary<string, bool> { ["X"] = false })["TIED.Y"].ShouldBe(true);
        network.Evaluate(new Dictionary<string, bool> { ["X"] = true })["TIED.Y"].ShouldBe(false);
    }

    [Fact]
    public void Build_SignalNameOnConnectedPin_IsIgnoredTheWireWins()
    {
        var source = NotInstance("SRC");
        var driven = NotInstance("INV", signalOfA: "S");
        var connections = new[] { Connect(source, "Y", driven, "A") };

        var network = new LogicNetworkBuilder().Build(new[] { source, driven }, connections);

        network.InputPinNames.ShouldBe(new[] { "SRC.A" },
            "a driven pin is no network input — its signal name never registers");
        network.FanOutWarnings.ShouldBeEmpty();
    }

    [Fact]
    public void Build_SignalNameOnNonInputPin_ThrowsNamingThePin()
    {
        var invalid = new LogicGateInstance(
            CreateGateGroup("NAND", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(
                new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold,
                new Dictionary<string, string> { ["Y"] = "S" }));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { invalid }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'Y'");
        error.Message.ShouldContain("not one of its input pins");
    }

    [Fact]
    public void Build_EmptySignalName_ThrowsNamingThePin()
    {
        var invalid = new LogicGateInstance(
            CreateGateGroup("NAND", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(
                new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold,
                new Dictionary<string, string> { ["A"] = "  " }));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { invalid }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'A'");
        error.Message.ShouldContain("empty signal name");
    }

    /// <summary>A NAND gate instance on a synthetic group exposing the example's pin interface.</summary>
    private static LogicGateInstance NandInstance(string groupName, string? signalOfA = null, string? signalOfB = null) =>
        new(
            CreateGateGroup(groupName, "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(
                new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold,
                SignalNames(("A", signalOfA), ("B", signalOfB))));

    /// <summary>A NOT gate instance on a synthetic group exposing the example's pin interface.</summary>
    private static LogicGateInstance NotInstance(string groupName, string? signalOfA = null) =>
        new(
            CreateGateGroup(groupName, "A", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(
                new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold,
                SignalNames(("A", signalOfA))));

    /// <summary>The signal-name map for a role assignment, or null when no pin carries a name.</summary>
    private static Dictionary<string, string>? SignalNames(params (string Pin, string? Signal)[] pins)
    {
        var named = pins.Where(p => p.Signal != null).ToDictionary(p => p.Pin, p => p.Signal!);
        return named.Count > 0 ? named : null;
    }

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

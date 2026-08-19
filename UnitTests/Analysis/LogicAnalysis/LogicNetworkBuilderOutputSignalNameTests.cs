using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Output signal names (issue #1046): a gate output pin carrying a persisted signal
/// name surfaces as a network-level output tap under that name — the adder's sum
/// reads <c>S</c>, its carry <c>Cout</c>, not <c>H2SUM.Y</c>/<c>OROUT.Y</c> — while
/// an unnamed output keeps its raw <c>&lt;gate&gt;.&lt;pin&gt;</c> tap name. Unlike
/// input names, output names never merge (every tap is one gate output), so two
/// outputs named alike are rejected at build time, as are empty names and names on
/// non-output pins.
/// </summary>
public class LogicNetworkBuilderOutputSignalNameTests
{
    [Fact]
    public void Build_NamedOutputTap_ReadsUnderSignalName()
    {
        var gate = NotInstance("INV", outputSignal: "Done");

        var network = new LogicNetworkBuilder().Build(
            new[] { gate }, Array.Empty<WaveguideConnection>());

        network.OutputPinNames.ShouldBe(new[] { "Done" },
            "the named output's tap reads the signal name, not INV.Y");
        network.OutputTaps["Done"].ShouldBe(new LogicPinRef("INV", "Y"),
            "the tap still points at the raw gate output pin");
        var outputs = network.Evaluate(new Dictionary<string, bool> { ["INV.A"] = true });
        outputs["Done"].ShouldBe(false, "evaluation results key by the tap's signal name");
    }

    [Fact]
    public void Build_UnnamedOutputAlongsideNamed_KeepsRawGateDotPinName()
    {
        var named = NotInstance("INV1", outputSignal: "S");
        var unnamed = NotInstance("INV2");

        var network = new LogicNetworkBuilder().Build(
            new[] { named, unnamed }, Array.Empty<WaveguideConnection>());

        network.OutputPinNames.ShouldBe(new[] { "S", "INV2.Y" }, ignoreOrder: true,
            "the unnamed output keeps its raw tap name");
    }

    [Fact]
    public void Build_NamedOutputDrivingAnotherGate_TapStillReadsSignalName()
    {
        // The full adder's Cout shape: the OR stage's output feeds nothing further
        // here, but a named output that *does* drive a gate keeps its tap name —
        // the wire decides the wiring, the name decides the reading.
        var source = NotInstance("SRC", outputSignal: "Carry");
        var sink = NotInstance("SINK");
        var connections = new[] { Connect(source, "Y", sink, "A") };

        var network = new LogicNetworkBuilder().Build(new[] { source, sink }, connections);

        network.OutputPinNames.ShouldBe(new[] { "Carry", "SINK.Y" }, ignoreOrder: true,
            "a driven output stays a tap and reads its signal name");
        var outputs = network.Evaluate(new Dictionary<string, bool> { ["SRC.A"] = false });
        outputs["Carry"].ShouldBe(true);
        outputs["SINK.Y"].ShouldBe(false, "the named tap's bit drives the sink as before");
    }

    [Fact]
    public void Build_DuplicateOutputSignalNames_ThrowsNamingBothPins()
    {
        var first = NotInstance("INV1", outputSignal: "S");
        var second = NotInstance("INV2", outputSignal: "S");

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { first, second }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'S'");
        error.Message.ShouldContain("INV1.Y");
        error.Message.ShouldContain("INV2.Y");
    }

    [Fact]
    public void Build_OutputSignalNameOnNonOutputPin_ThrowsNamingThePin()
    {
        var invalid = new LogicGateInstance(
            CreateGateGroup("NAND", "A", "B", "BIAS", "Y"),
            PinnedGateTables.NandGate(),
            new GateRoleAssignment(
                new[] { "A", "B" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NandThreshold,
                OutputSignalNames: new Dictionary<string, string> { ["A"] = "S" }));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { invalid }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'A'");
        error.Message.ShouldContain("not one of its output pins");
    }

    [Fact]
    public void Build_EmptyOutputSignalName_ThrowsNamingThePin()
    {
        var invalid = new LogicGateInstance(
            CreateGateGroup("NOT", "A", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(
                new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold,
                OutputSignalNames: new Dictionary<string, string> { ["Y"] = "  " }));

        var error = Should.Throw<ArgumentException>(
            () => new LogicNetworkBuilder().Build(new[] { invalid }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'Y'");
        error.Message.ShouldContain("empty signal name");
    }

    /// <summary>A NOT gate instance on a synthetic group exposing the example's pin interface.</summary>
    private static LogicGateInstance NotInstance(string groupName, string? outputSignal = null) =>
        new(
            CreateGateGroup(groupName, "A", "BIAS", "Y"),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(
                new[] { "A" }, new[] { "Y" }, new[] { "BIAS" }, PinnedGateTables.NotThreshold,
                OutputSignalNames: outputSignal == null
                    ? null
                    : new Dictionary<string, string> { ["Y"] = outputSignal }));

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

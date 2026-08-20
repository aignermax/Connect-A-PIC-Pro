using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Register designation at the design-to-network assembly level: a feedback loop
/// between two OR gate groups assembles once one of them carries the persisted
/// register flag on its <see cref="TruthTablePinAssignment"/>, and the very same
/// loop without the designation keeps the combinational-cycle rejection. The
/// assembled network then evaluates two-phase — the register gate's output holds
/// its committed value until <see cref="LogicNetworkEvaluator.Step"/> samples the
/// loop — straight from the persisted assignment the .lun round trip delivers.
/// </summary>
public class LogicNetworkAssemblerRegisterTests
{
    private const double OrThreshold = 0.25;

    [Fact]
    public async Task AssembleAsync_FeedbackLoopThroughDesignatedRegister_AssemblesAndSteps()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2", isRegister: true);
        var connections = FeedbackLoop(first, second);

        var network = await Assemble(new Component[] { first, second }, connections);

        network.RegisterState.Keys.ShouldBe(new[] { new LogicPinRef("OR2", "y") },
            "only the designated gate carries committed state");

        // OR1.y = OR(OR2.y_committed, OR1.b): with OR1.b on, OR1.y rises while the
        // register output still holds its cleared power-up value.
        var settled = network.Evaluate(Bits(("OR1.b", true), ("OR2.b", false)));
        settled["OR1.y"].ShouldBeTrue("the combinational gate settles from the committed 0");
        settled["OR2.y"].ShouldBeFalse("the register output holds until the step");

        network.Step();
        var stepped = network.Evaluate(Bits(("OR1.b", true), ("OR2.b", false)));
        stepped["OR2.y"].ShouldBeTrue("the step sampled OR1.y=1 around the loop and committed it");
        stepped["OR1.y"].ShouldBeTrue();

        // The committed bit holds the loop on its own once OR1.b goes low again.
        network.Evaluate(Bits(("OR1.b", false), ("OR2.b", false)))["OR2.y"].ShouldBeTrue(
            "changing the inputs without a step must not change the committed output");
    }

    [Fact]
    public async Task AssembleAsync_SameLoopWithoutRegisterDesignation_KeepsTheCycleRejection()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var connections = FeedbackLoop(first, second);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => Assemble(new Component[] { first, second }, connections));

        error.Message.ShouldContain("cycle");
        error.Message.ShouldContain("OR1");
        error.Message.ShouldContain("OR2");
        error.Message.ShouldContain("sequential logic is not supported");
    }

    /// <summary>The cross-wired feedback loop: OR1.y → OR2.a and OR2.y → OR1.a.</summary>
    private static WaveguideConnection[] FeedbackLoop(ComponentGroup first, ComponentGroup second) =>
        new[] { Connect(first, "y", second, "a"), Connect(second, "y", first, "a") };

    /// <summary>Runs the assembler at the fixture wavelength.</summary>
    private static Task<LogicNetworkEvaluator> Assemble(
        IReadOnlyList<Component> components, IReadOnlyList<WaveguideConnection> connections) =>
        new LogicNetworkAssembler().AssembleAsync(
            components, connections, LogicGateFixtureFactory.WavelengthNm);

    /// <summary>
    /// A combiner group with the OR-reading assignment persisted, as a save → load
    /// round trip would deliver it — optionally carrying the register designation.
    /// </summary>
    private static ComponentGroup OrGate(string groupName, bool isRegister = false)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "a", "b" },
            OutputPinNames = new List<string> { "y" },
            BiasPinNames = new List<string>(),
            Threshold = OrThreshold,
            IsRegister = isRegister,
        };
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = Pin(from, fromPin), EndPin = Pin(to, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}

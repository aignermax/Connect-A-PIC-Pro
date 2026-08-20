using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The register state element of the logic layer (sequential slice 1): a gate
/// designated as a register holds its committed output through combinational
/// settling and only an explicit <see cref="LogicNetworkEvaluator.Step"/> samples
/// and commits its inputs (D-semantics). A feedback cycle is legal exactly when it
/// passes through at least one register; a purely combinational cycle keeps the
/// honest rejection. Covered at the net-list level with value-built gate models:
/// a D-flip-flop (buffer register), an SR latch from two cross-coupled NAND
/// registers, and the cycle-legality boundary.
/// </summary>
public class LogicNetworkRegisterTests
{
    [Fact]
    public void Constructor_FeedbackLoopThroughRegister_Assembles()
    {
        var network = ToggleLoop(registerGateIds: new[] { "reg" });

        network.Gates.Keys.ShouldBe(new[] { "reg", "inv" });
        network.RegisterState.Keys.ShouldBe(new[] { new LogicPinRef("reg", "Y") },
            "the register powers up with its committed output cleared");
    }

    [Fact]
    public void Constructor_SameLoopWithoutRegisterDesignation_KeepsTheCombinationalCycleRejection()
    {
        var error = Should.Throw<InvalidOperationException>(() => ToggleLoop(registerGateIds: null));

        error.Message.ShouldContain("cycle");
        error.Message.ShouldContain("reg");
        error.Message.ShouldContain("inv");
        error.Message.ShouldContain("sequential logic is not supported");
    }

    [Fact]
    public void Constructor_CombinationalCycleBesideARegister_KeepsTheCombinationalCycleRejection()
    {
        // A register elsewhere in the network must not pardon a cycle that passes
        // through combinational gates only.
        var error = Should.Throw<InvalidOperationException>(() => new LogicNetworkEvaluator(
            new[] { "d" },
            new Dictionary<string, LogicGateModel>
            {
                ["reg"] = BufferGate(),
                ["n1"] = PinnedGateTables.NotGate(),
                ["n2"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.NetworkInput("d"),
                [new LogicPinRef("n1", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("n2", "Y")),
                [new LogicPinRef("n2", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("n1", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: new[] { "reg" }));

        error.Message.ShouldContain("cycle");
        error.Message.ShouldContain("n1");
        error.Message.ShouldContain("n2");
        error.Message.ShouldContain("sequential logic is not supported");
    }

    [Fact]
    public void Constructor_UnknownRegisterGateId_ThrowsNamingTheGate()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "d" },
            new Dictionary<string, LogicGateModel> { ["reg"] = BufferGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.NetworkInput("d"),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: new[] { "ghost" }));

        error.Message.ShouldContain("unknown gate 'ghost'");
        error.Message.ShouldContain("Known gates: reg");
    }

    [Fact]
    public void Evaluate_DFlipFlop_HoldsCommittedOutputUntilStepCommitsTheSampledInput()
    {
        var network = DFlipFlop();

        network.Evaluate(Bits(("d", true)))["q"].ShouldBeFalse(
            "a register powers up cleared — D=1 is not committed yet");
        network.RegisterState[new LogicPinRef("reg", "Y")].ShouldBeFalse();

        network.Step();
        network.Evaluate(Bits(("d", true)))["q"].ShouldBeTrue(
            "the step sampled D=1 and committed it");

        network.Evaluate(Bits(("d", false)))["q"].ShouldBeTrue(
            "changing D without a step must not change the committed output");

        network.Step();
        network.Evaluate(Bits(("d", false)))["q"].ShouldBeFalse(
            "the second step commits the new input");
    }

    [Fact]
    public void Step_ConsecutiveSteps_AdvanceConsecutiveClocks()
    {
        // The toggle loop reg = NOT(reg) through an inverter: every step flips the
        // committed bit — the feedback cycle the designation made legal. The
        // declared network input "x" drives nothing (the loop is self-sufficient),
        // but Evaluate still requires its bit.
        var network = ToggleLoop(registerGateIds: new[] { "reg" });

        network.Evaluate(Bits(("x", false)))["q"].ShouldBeFalse("powered up cleared");
        network.Step();
        network.Evaluate(Bits(("x", false)))["q"].ShouldBeTrue("first clock: NOT(0) committed");
        network.Step();
        network.Evaluate(Bits(("x", false)))["q"].ShouldBeFalse("second clock: NOT(1) committed");
    }

    [Fact]
    public void Constructor_SelfSufficientRegisterLoopWithoutInputs_Assembles()
    {
        // A register-bearing network needs no network input: Step is its only
        // stimulus (a free-running counter). The toggle loop still flips per clock.
        var network = SelfSufficientToggleLoop();

        network.InputPinNames.ShouldBeEmpty();
        network.Evaluate(Bits())["q"].ShouldBeFalse("powered up cleared");
        network.Step();
        network.Evaluate(Bits())["q"].ShouldBeTrue("first clock: NOT(0) committed");
        network.Step();
        network.Evaluate(Bits())["q"].ShouldBeFalse("second clock: NOT(1) committed");
    }

    [Fact]
    public void Constructor_CombinationalNetworkWithoutInputs_KeepsTheEmptyInputRejection()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            Array.Empty<string>(),
            new Dictionary<string, LogicGateModel> { ["buf"] = BufferGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("buf", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("buf", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("buf", "Y") }));

        error.Message.ShouldContain("at least one input pin");
    }

    [Fact]
    public void Step_BeforeAnyEvaluate_ThrowsTellingTheCallerToSettleFirst()
    {
        var error = Should.Throw<InvalidOperationException>(() => DFlipFlop().Step());

        error.Message.ShouldContain("Evaluate");
    }

    [Fact]
    public void Evaluate_SrLatchFromCrossCoupledNandRegisters_HoldsStateAcrossSteps()
    {
        var network = SrLatch();

        // Set (active-low S): Q rises, Q̄ falls — and then holds while both
        // inputs rest at 1.
        network.Evaluate(Bits(("s", false), ("r", true)));
        network.Step();
        network.Step();
        network.Evaluate(Bits(("s", true), ("r", true)))["q"].ShouldBeTrue("the latch is set");
        network.Evaluate(Bits(("s", true), ("r", true)))["qb"].ShouldBeFalse();

        network.Step();
        network.Step();
        network.Evaluate(Bits(("s", true), ("r", true)))["q"].ShouldBeTrue(
            "resting inputs hold the set state");

        // Reset (active-low R): Q falls, Q̄ rises — and holds again.
        network.Evaluate(Bits(("s", true), ("r", false)));
        network.Step();
        network.Step();
        network.Evaluate(Bits(("s", true), ("r", true)))["q"].ShouldBeFalse("the latch is reset");
        network.Evaluate(Bits(("s", true), ("r", true)))["qb"].ShouldBeTrue();

        network.Step();
        network.Step();
        network.Evaluate(Bits(("s", true), ("r", true)))["q"].ShouldBeFalse(
            "resting inputs hold the reset state");
    }

    /// <summary>One buffer register with D as its only network input and Q as its tap.</summary>
    private static LogicNetworkEvaluator DFlipFlop() =>
        new(
            new[] { "d" },
            new Dictionary<string, LogicGateModel> { ["reg"] = BufferGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.NetworkInput("d"),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: new[] { "reg" });

    /// <summary>
    /// The smallest feedback loop: an inverter wired between the register's output
    /// and input (reg = NOT(reg) once clocked). Without the register designation
    /// this is the rejected combinational cycle.
    /// </summary>
    private static LogicNetworkEvaluator ToggleLoop(IReadOnlyCollection<string>? registerGateIds) =>
        new(
            new[] { "x" },
            new Dictionary<string, LogicGateModel>
            {
                ["reg"] = BufferGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("inv", "Y")),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("reg", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: registerGateIds);

    /// <summary>
    /// Two cross-coupled NAND gates, both designated registers: Q = NAND(S, Q̄),
    /// Q̄ = NAND(R, Q) with active-low set/reset — the classic SR latch.
    /// </summary>
    private static LogicNetworkEvaluator SrLatch() =>
        new(
            new[] { "s", "r" },
            new Dictionary<string, LogicGateModel>
            {
                ["qGate"] = PinnedGateTables.NandGate(),
                ["qbGate"] = PinnedGateTables.NandGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("qGate", "A")] = new LogicNetDriver.NetworkInput("s"),
                [new LogicPinRef("qGate", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("qbGate", "Y")),
                [new LogicPinRef("qbGate", "A")] = new LogicNetDriver.NetworkInput("r"),
                [new LogicPinRef("qbGate", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("qGate", "Y")),
            },
            new Dictionary<string, LogicPinRef>
            {
                ["q"] = new("qGate", "Y"),
                ["qb"] = new("qbGate", "Y"),
            },
            registerGateIds: new[] { "qGate", "qbGate" });

    /// <summary>
    /// The toggle loop with no network input at all: the register and the inverter
    /// feed each other, so <see cref="LogicNetworkEvaluator.Step"/> is the only stimulus.
    /// </summary>
    private static LogicNetworkEvaluator SelfSufficientToggleLoop() =>
        new(
            Array.Empty<string>(),
            new Dictionary<string, LogicGateModel>
            {
                ["reg"] = BufferGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("reg", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("inv", "Y")),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("reg", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["q"] = new("reg", "Y") },
            registerGateIds: new[] { "reg" });

    /// <summary>The D-flip-flop's combinational core: a buffer, Y = A.</summary>
    private static LogicGateModel BufferGate()
    {
        var inputs = new[] { "A" };
        var rows = new[] { false, true }
            .Select(bit => new TruthTableRow(
                new Dictionary<string, bool> { ["A"] = bit },
                new Dictionary<string, LogicOutputValue> { ["Y"] = new(bit, bit ? 0.5 : 0.0) }))
            .ToArray();
        return LogicGateModel.FromTruthTable(
            new TruthTable("Buffer", inputs, new[] { "Y" }, 0.25, PinnedGateTables.WavelengthNm, rows));
    }

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}

using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The logic-level network evaluator: gates from the pinned NAND/NOT tables
/// composed into bigger circuits — AND = NOT(NAND), OR = NAND(NOT a, NOT b), and
/// a four-stage inverter chain. Every stage output is a clean bit, so arbitrary
/// cascade depth works at the logic layer, exactly what the passive-linear layer
/// cannot do (its margin tops out at 2× after two stages). Also covers cycle
/// detection and the wiring error paths.
/// </summary>
public class LogicNetworkEvaluatorTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Evaluate_AndFromNandFeedingNot_MatchesTheAndTruthTable(bool a, bool b, bool expected)
    {
        var network = new LogicNetworkEvaluator(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["nand"] = PinnedGateTables.NandGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("nand", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") });

        network.Evaluate(Bits(("a", a), ("b", b))).ShouldBe(Bits(("y", expected)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Evaluate_OrFromNandWithInvertedInputs_MatchesTheOrTruthTable(bool a, bool b, bool expected)
    {
        var network = new LogicNetworkEvaluator(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel>
            {
                ["notA"] = PinnedGateTables.NotGate(),
                ["notB"] = PinnedGateTables.NotGate(),
                ["nand"] = PinnedGateTables.NandGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("notA", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("notB", "A")] = new LogicNetDriver.NetworkInput("b"),
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("notA", "Y")),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.GateOutput(new LogicPinRef("notB", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") });

        network.Evaluate(Bits(("a", a), ("b", b))).ShouldBe(Bits(("y", expected)));
    }

    [Theory]
    [InlineData(false, new[] { true, false, true, false })]
    [InlineData(true, new[] { false, true, false, true })]
    public void Evaluate_FourStageNotChain_RestoresCleanLevelsAtEveryStage(bool x, bool[] expectedStages)
    {
        var wiring = new Dictionary<LogicPinRef, LogicNetDriver>
        {
            [new LogicPinRef("stage1", "A")] = new LogicNetDriver.NetworkInput("x"),
        };
        var taps = new Dictionary<string, LogicPinRef>();
        for (var stage = 1; stage <= 4; stage++)
        {
            if (stage > 1)
                wiring[new LogicPinRef($"stage{stage}", "A")] =
                    new LogicNetDriver.GateOutput(new LogicPinRef($"stage{stage - 1}", "Y"));
            taps[$"y{stage}"] = new LogicPinRef($"stage{stage}", "Y");
        }

        var network = new LogicNetworkEvaluator(
            new[] { "x" },
            Enumerable.Range(1, 4).ToDictionary(stage => $"stage{stage}", _ => PinnedGateTables.NotGate()),
            wiring,
            taps);

        var outputs = network.Evaluate(Bits(("x", x)));

        for (var stage = 1; stage <= 4; stage++)
            outputs[$"y{stage}"].ShouldBe(expectedStages[stage - 1],
                $"stage {stage} output is a clean bit — ideal level restoration at any cascade depth");
        outputs["y4"].ShouldBe(x, "four inversions restore the input exactly");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Evaluate_NetworkInputFannedOutToBothNandInputs_ActsAsNot(bool a, bool expected)
    {
        var network = new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["nand"] = PinnedGateTables.NandGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.NetworkInput("a"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") });

        network.Evaluate(Bits(("a", a))).ShouldBe(Bits(("y", expected)));
    }

    [Fact]
    public void Constructor_CrossWiredGates_ThrowsCycleErrorInsteadOfHanging()
    {
        var error = Should.Throw<InvalidOperationException>(() => new LogicNetworkEvaluator(
            new[] { "x" },
            new Dictionary<string, LogicGateModel>
            {
                ["n1"] = PinnedGateTables.NotGate(),
                ["n2"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("n1", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("n2", "Y")),
                [new LogicPinRef("n2", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("n1", "Y")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("n1", "Y") }));

        error.Message.ShouldContain("cycle");
        error.Message.ShouldContain("n1");
        error.Message.ShouldContain("n2");
        error.Message.ShouldContain("sequential logic is not supported");
    }

    [Fact]
    public void Constructor_WiringTargetsUnknownGate_ThrowsNamingTheGate()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["nand"] = PinnedGateTables.NandGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("ghost", "A")] = new LogicNetDriver.NetworkInput("a"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") }));

        error.Message.ShouldContain("unknown gate 'ghost'");
        error.Message.ShouldContain("Known gates: nand");
    }

    [Fact]
    public void Constructor_WiringTargetsUnknownInputPin_ThrowsListingAvailablePins()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a", "b" },
            new Dictionary<string, LogicGateModel> { ["nand"] = PinnedGateTables.NandGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "C")] = new LogicNetDriver.NetworkInput("b"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") }));

        error.Message.ShouldContain("Gate 'nand' has no input pin 'C'");
        error.Message.ShouldContain("Available inputs: A, B");
    }

    [Fact]
    public void Constructor_DriverFromUnknownNetworkInput_ThrowsListingDeclaredInputs()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["inv"] = PinnedGateTables.NotGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.NetworkInput("c"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") }));

        error.Message.ShouldContain("no input pin 'c'");
        error.Message.ShouldContain("Declared inputs: a");
    }

    [Fact]
    public void Constructor_DriverFromNonOutputPin_ThrowsListingAvailableOutputs()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel>
            {
                ["nand"] = PinnedGateTables.NandGate(),
                ["inv"] = PinnedGateTables.NotGate(),
            },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("nand", "B")] = new LogicNetDriver.NetworkInput("a"),
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.GateOutput(new LogicPinRef("nand", "A")),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") }));

        error.Message.ShouldContain("Gate 'nand' has no output pin 'A'");
        error.Message.ShouldContain("Available outputs: Y");
    }

    [Fact]
    public void Constructor_UndrivenGateInput_ThrowsNamingThePin()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["nand"] = PinnedGateTables.NandGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("nand", "A")] = new LogicNetDriver.NetworkInput("a"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("nand", "Y") }));

        error.Message.ShouldContain("Gate 'nand' has undriven input pins: B");
    }

    [Fact]
    public void Constructor_OutputTapOnNonOutputPin_Throws()
    {
        var error = Should.Throw<ArgumentException>(() => new LogicNetworkEvaluator(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["inv"] = PinnedGateTables.NotGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.NetworkInput("a"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "A") }));

        error.Message.ShouldContain("Network output 'y'");
        error.Message.ShouldContain("not an output");
    }

    [Fact]
    public void Evaluate_MissingNetworkInputBit_ThrowsNamingTheInput()
    {
        var network = SingleNotNetwork();

        var error = Should.Throw<ArgumentException>(
            () => network.Evaluate(new Dictionary<string, bool>()));

        error.Message.ShouldContain("No bit provided for network input 'a'");
    }

    [Fact]
    public void Evaluate_UnknownNetworkInputBit_ThrowsNamingTheInput()
    {
        var network = SingleNotNetwork();

        var error = Should.Throw<ArgumentException>(
            () => network.Evaluate(Bits(("a", true), ("c", false))));

        error.Message.ShouldContain("no input pin 'c'");
    }

    /// <summary>The smallest valid network: one inverter driven by one network input.</summary>
    private static LogicNetworkEvaluator SingleNotNetwork() =>
        new(
            new[] { "a" },
            new Dictionary<string, LogicGateModel> { ["inv"] = PinnedGateTables.NotGate() },
            new Dictionary<LogicPinRef, LogicNetDriver>
            {
                [new LogicPinRef("inv", "A")] = new LogicNetDriver.NetworkInput("a"),
            },
            new Dictionary<string, LogicPinRef> { ["y"] = new("inv", "Y") });

    /// <summary>Builds an input-bit dictionary from (name, bit) pairs.</summary>
    private static Dictionary<string, bool> Bits(params (string Name, bool Bit)[] bits) =>
        bits.ToDictionary(pair => pair.Name, pair => pair.Bit);
}

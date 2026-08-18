using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// The design-to-network assembler over the numeric fixtures: a top-level group
/// carrying a persisted <see cref="TruthTablePinAssignment"/> becomes a gate whose
/// model is re-extracted with the persisted roles — the 50/50 combiner group plays
/// an OR gate at threshold 0.25, so a wired cascade must evaluate as a three-input
/// OR by pure table lookup. Groups without an assignment (and non-group components)
/// are ignored, a design without any gate fails readably, and builder errors pass
/// through with their pin names intact.
/// </summary>
public class LogicNetworkAssemblerTests
{
    private const double OrThreshold = 0.25;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task AssembleAsync_OutputWiredToInput_EvaluatesTheCascadedNetworkFromTheCanvasConnection(
        bool a, bool b, bool c)
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var connections = new[] { Connect(first, "y", second, "a") };

        var network = await Assemble(new Component[] { first, second }, connections);

        network.InputPinNames.ShouldBe(new[] { "OR1.a", "OR1.b", "OR2.b" },
            "the unconnected gate inputs become the network-level inputs");
        network.OutputPinNames.ShouldBe(new[] { "OR1.y", "OR2.y" });
        var outputs = network.Evaluate(Bits(("OR1.a", a), ("OR1.b", b), ("OR2.b", c)));
        outputs["OR2.y"].ShouldBe(a || b || c, $"OR2.y = OR(OR(a, b), c) for a={a}, b={b}, c={c}");
        outputs["OR1.y"].ShouldBe(a || b, $"OR1.y stays readable as a tap for a={a}, b={b}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AssembleAsync_GroupWithoutAssignmentAndNonGroupComponent_AreIgnored(bool a, bool b)
    {
        var gate = OrGate("OR1");
        var unassigned = LogicGateFixtureFactory.CreateCombinerGroup();
        unassigned.GroupName = "PLAIN";
        var nonGroup = unassigned.ChildComponents.Single();

        var network = await Assemble(
            new Component[] { gate, unassigned, nonGroup }, Array.Empty<WaveguideConnection>());

        network.Gates.Keys.ShouldBe(new[] { "OR1" },
            "only the group with a persisted assignment is a gate");
        network.Evaluate(Bits(("OR1.a", a), ("OR1.b", b)))["OR1.y"]
            .ShouldBe(a || b, $"the remaining gate still evaluates for a={a}, b={b}");
    }

    [Fact]
    public async Task AssembleAsync_OutputFannedOutToTwoInputs_ReportsTheFanOutWarning()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var connections = new[] { Connect(first, "y", second, "a"), Connect(first, "y", second, "b") };

        var network = await Assemble(new Component[] { first, second }, connections);

        var warning = network.FanOutWarnings.ShouldHaveSingleItem();
        warning.PinName.ShouldBe("OR1.y");
        warning.LoadCount.ShouldBe(2);
        network.Evaluate(Bits(("OR1.a", true), ("OR1.b", false)))["OR2.y"].ShouldBeTrue(
            "the fan-out stays evaluable — the warning does not block the idealized result");
    }

    [Fact]
    public async Task AssembleAsync_PointToPointWiring_ReportsNoFanOutWarnings()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var connections = new[] { Connect(first, "y", second, "a") };

        var network = await Assemble(new Component[] { first, second }, connections);

        network.FanOutWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssembleAsync_NoGateGroup_ThrowsAReadableError()
    {
        var plain = new ComponentGroup("PLAIN");

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => Assemble(new Component[] { plain }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("no logic gate");
        error.Message.ShouldContain("truth-table pin assignment");
    }

    [Fact]
    public async Task AssembleAsync_EmptyDesign_ThrowsAReadableError()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => Assemble(Array.Empty<Component>(), Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("no logic gate");
    }

    [Fact]
    public async Task AssembleAsync_DuplicateGateNames_PassesTheBuilderErrorThrough()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR1");

        var error = await Should.ThrowAsync<ArgumentException>(
            () => Assemble(new Component[] { first, second }, Array.Empty<WaveguideConnection>()));

        error.Message.ShouldContain("'OR1'");
        error.Message.ShouldContain("must be unique");
    }

    [Fact]
    public async Task AssembleAsync_OutputToOutputWiring_PassesTheBuilderErrorThroughNamingThePins()
    {
        var first = OrGate("OR1");
        var second = OrGate("OR2");
        var connections = new[] { Connect(first, "y", second, "y") };

        var error = await Should.ThrowAsync<ArgumentException>(
            () => Assemble(new Component[] { first, second }, connections));

        error.Message.ShouldContain("two gate output pins");
        error.Message.ShouldContain("OR1.y");
        error.Message.ShouldContain("OR2.y");
    }

    [Fact]
    public async Task AssembleAsync_NullComponents_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => new LogicNetworkAssembler().AssembleAsync(
                null!, Array.Empty<WaveguideConnection>(), LogicGateFixtureFactory.WavelengthNm));
    }

    [Fact]
    public async Task AssembleAsync_NullConnections_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => new LogicNetworkAssembler().AssembleAsync(
                Array.Empty<Component>(), null!, LogicGateFixtureFactory.WavelengthNm));
    }

    /// <summary>Runs the assembler at the fixture wavelength.</summary>
    private static Task<LogicNetworkEvaluator> Assemble(
        IReadOnlyList<Component> components, IReadOnlyList<WaveguideConnection> connections) =>
        new LogicNetworkAssembler().AssembleAsync(
            components, connections, LogicGateFixtureFactory.WavelengthNm);

    /// <summary>
    /// A combiner group with the OR-reading assignment persisted, as a save → load
    /// round trip would deliver it; the S-matrix sync surfaces the connectable pins.
    /// </summary>
    private static ComponentGroup OrGate(string groupName)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "a", "b" },
            OutputPinNames = new List<string> { "y" },
            BiasPinNames = new List<string>(),
            Threshold = OrThreshold,
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

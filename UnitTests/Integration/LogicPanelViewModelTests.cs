using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel (issue #989, rung 4→5 of the NAND game): the
/// shipped <c>examples/Logic Gate Half Adder.lun</c> loads through the real load path,
/// the Build command assembles its logic network through
/// <see cref="CAP_Core.Analysis.LogicAnalysis.LogicNetworkAssembler"/>, the eight
/// network inputs (the addends A and B fanned out to four gate pins each) appear as
/// toggles and every gate output as a live 0/1 indicator — toggling all pins of one
/// addend shows Sum at <c>NAND4.Y</c> and Carry at <c>NOT1.Y</c> for all four input
/// combinations. A design without gate groups fails with a readable status instead of
/// an exception, and IsProcessing brackets the assembly in success and failure alike.
/// Fan-out honesty: the half adder's internal wires are all point-to-point, so it raises
/// no warning, while a gate output wired to two gate inputs surfaces a non-blocking
/// warning naming pin and load count — and the network still evaluates.
/// </summary>
public class LogicPanelViewModelTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    /// <summary>Network inputs driven by addend A (fan-out at the logic layer).</summary>
    private static readonly string[] InputsA = { "NAND1A.A", "NAND1B.A", "NAND2.A", "NAND5.A" };

    /// <summary>Network inputs driven by addend B (fan-out at the logic layer).</summary>
    private static readonly string[] InputsB = { "NAND1A.B", "NAND1B.B", "NAND3.B", "NAND5.B" };

    private static readonly string[] OutputTaps =
        { "NAND1A.Y", "NAND1B.Y", "NAND2.Y", "NAND3.Y", "NAND4.Y", "NAND5.Y", "NOT1.Y" };

    private readonly LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicPanelViewModelTests(LoadedHalfAdder fixture) => _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_HalfAdder_TogglingAddendPins_ShowsSumAndCarry()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.Inputs.Select(i => i.PinName).ShouldBe(
            InputsA.Concat(InputsB).ToArray(), ignoreOrder: true,
            customMessage: "the addends A and B fan out to four gate inputs each");
        vm.Outputs.Select(o => o.PinName).ShouldBe(OutputTaps, ignoreOrder: true);
        vm.Inputs.ShouldAllBe(i => !i.IsOn, "network inputs start off");

        foreach (var (a, b, expectedSum, expectedCarry) in new[]
                 {
                     (false, false, false, false),
                     (true, false, true, false),
                     (false, true, true, false),
                     (true, true, false, true),
                 })
        {
            SetAddend(vm, InputsA, a);
            SetAddend(vm, InputsB, b);

            vm.Outputs.Single(o => o.PinName == "NAND4.Y").IsOne.ShouldBe(expectedSum,
                $"Sum = A XOR B for A={a}, B={b}");
            vm.Outputs.Single(o => o.PinName == "NOT1.Y").IsOne.ShouldBe(expectedCarry,
                $"Carry = A AND B for A={a}, B={b}");
        }
    }

    [Fact]
    public async Task BuildNetwork_HalfAdder_ReportsProcessingDuringAssembly()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        var observed = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogicPanelViewModel.IsProcessing))
                observed.Add(vm.IsProcessing);
        };

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        observed.ShouldBe(new List<bool> { true, false },
            "IsProcessing brackets the assembly: on at the start, off when done");
        vm.IsProcessing.ShouldBeFalse();
    }

    [Fact]
    public async Task BuildNetwork_EmptyDesign_ShowsReadableErrorWithoutCrashing()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(new DesignCanvasViewModel());

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse("the processing flag resets in the failure path, too");
        vm.StatusText.ShouldContain("no logic gate");
    }

    [Fact]
    public async Task BuildNetwork_DesignWithoutGateGroups_ShowsReadableError()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(TestComponentFactory.CreateStraightWaveGuide()));
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeFalse();
        vm.IsProcessing.ShouldBeFalse();
        vm.StatusText.ShouldContain("no logic gate");
        vm.Inputs.ShouldBeEmpty();
        vm.Outputs.ShouldBeEmpty();
    }

    /// <summary>Sets every network input pin of one addend to the same bit.</summary>
    private static void SetAddend(LogicPanelViewModel vm, IEnumerable<string> pinNames, bool bit)
    {
        foreach (var name in pinNames)
            vm.Inputs.Single(i => i.PinName == name).IsOn = bit;
    }

    [Fact]
    public async Task BuildNetwork_HalfAdder_ReportsNoFanOutWarnings()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasFanOutWarnings.ShouldBeFalse(
            "the shipped example keeps every internal wire point-to-point and duplicates " +
            "its stages instead of splitting — its input fan-out lives in the tied toggles");
        vm.FanOutWarnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildNetwork_GateOutputFannedOutToTwoInputs_ShowsWarningNamingPinAndLoadCount()
    {
        var canvas = new DesignCanvasViewModel();
        var or1 = OrGate("OR1");
        var or2 = OrGate("OR2");
        canvas.Components.Add(new ComponentViewModel(or1));
        canvas.Components.Add(new ComponentViewModel(or2));
        canvas.Connections.Add(new WaveguideConnectionViewModel(Connect(or1, "y", or2, "a")));
        canvas.Connections.Add(new WaveguideConnectionViewModel(Connect(or1, "y", or2, "b")));
        var vm = new LogicPanelViewModel();
        vm.Configure(canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasFanOutWarnings.ShouldBeTrue();
        var warning = vm.FanOutWarnings.ShouldHaveSingleItem();
        warning.ShouldContain("OR1.y");
        warning.ShouldContain("2");

        vm.Inputs.Single(i => i.PinName == "OR1.a").IsOn = true;
        vm.Outputs.Single(o => o.PinName == "OR2.y").IsOne.ShouldBeTrue(
            "the warning does not block evaluation — the idealized result stays available");
    }

    /// <summary>A combiner group with the OR-reading assignment persisted, as the load path delivers it.</summary>
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

    private const double OrThreshold = 0.25;

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = Pin(from, fromPin), EndPin = Pin(to, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);

    /// <summary>
    /// Shared fixture: loads the shipped half-adder example through the real load path
    /// once; every test assembles its own network from that canvas via the panel VM.
    /// </summary>
    public class LoadedHalfAdder : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Half Adder.lun";

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>Loads the shipped example through the real load path.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;
    }
}

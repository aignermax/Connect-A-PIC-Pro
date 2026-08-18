using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
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
    public async Task BuildNetwork_HalfAdder_SurfacesFanOutWarningsForBothAddends()
    {
        // Issue #996: the half adder fans its addends out at the logic layer —
        // addend A drives the four pins NAND1A.A, NAND1B.A, NAND2.A, NAND5.A,
        // addend B drives four more — so the panel must surface one warning per
        // addend, naming the signal and the load count.
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.HasFanOutWarnings.ShouldBeTrue("the half adder's addends fan out to four gate inputs each");
        vm.FanOutWarnings.Count.ShouldBe(2);
        var warningA = vm.FanOutWarnings.Single(w => w.Warning.DriverDisplayName == "A");
        var warningB = vm.FanOutWarnings.Single(w => w.Warning.DriverDisplayName == "B");
        warningA.Warning.IsNetworkInputSignal.ShouldBeTrue();
        warningB.Warning.IsNetworkInputSignal.ShouldBeTrue();
        warningA.Warning.LoadCount.ShouldBe(4);
        warningB.Warning.LoadCount.ShouldBe(4);
        warningA.Warning.LoadNames.ShouldBe(InputsA, ignoreOrder: true);
        warningB.Warning.LoadNames.ShouldBe(InputsB, ignoreOrder: true);
        warningA.WarningText.ShouldContain("A");
        warningA.WarningText.ShouldContain("4");
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

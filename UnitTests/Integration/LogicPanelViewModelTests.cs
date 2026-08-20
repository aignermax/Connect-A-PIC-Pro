using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// ViewModel tests for the Logic panel (issue #989, rung 4→5 of the NAND game): the
/// shipped <c>examples/Logic Gate Half Adder.lun</c> loads through the real load path,
/// the Build command assembles its logic network through
/// <see cref="CAP_Core.Analysis.LogicAnalysis.LogicNetworkAssembler"/>, and the two
/// network inputs — the signals A and B (issue #1025), each merging the four gate
/// pins the addend fans out to — appear as one toggle per signal with every gate
/// output as a live 0/1 indicator: toggling A and B shows Sum at <c>NAND4.Y</c>
/// and Carry at <c>NOT1.Y</c> for all four input combinations. A design without
/// gate groups fails with a readable status instead of an exception, and
/// IsProcessing brackets the assembly in success and failure alike.
/// </summary>
public class LogicPanelViewModelTests : IClassFixture<LogicPanelViewModelTests.LoadedHalfAdder>
{
    /// <summary>Gate input pins addend A fans out to (the signal A's member pins).</summary>
    private static readonly string[] LoadsOfSignalA = { "NAND1A.A", "NAND1B.A", "NAND2.A", "NAND5.A" };

    /// <summary>Gate input pins addend B fans out to (the signal B's member pins).</summary>
    private static readonly string[] LoadsOfSignalB = { "NAND1A.B", "NAND1B.B", "NAND3.B", "NAND5.B" };

    private static readonly string[] OutputTaps =
        { "NAND1A.Y", "NAND1B.Y", "NAND2.Y", "NAND3.Y", "NAND4.Y", "NAND5.Y", "NOT1.Y" };

    private readonly LoadedHalfAdder _fixture;

    /// <summary>Attaches the shared loaded half-adder canvas.</summary>
    public LogicPanelViewModelTests(LoadedHalfAdder fixture) => _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_HalfAdder_TogglingAddendSignals_ShowsSumAndCarry()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.Inputs.Select(i => i.PinName).ShouldBe(new[] { "A", "B" }, ignoreOrder: true,
            customMessage: "one toggle per signal (#1025) — the eight operand pins merge into A and B");
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
            vm.Inputs.Single(i => i.PinName == "A").IsOn = a;
            vm.Inputs.Single(i => i.PinName == "B").IsOn = b;

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
        // Issue #996 + #1025: the half adder fans its addends out at the logic
        // layer — signal A drives the four pins NAND1A.A, NAND1B.A, NAND2.A,
        // NAND5.A, signal B drives four more — so the panel must surface one
        // warning per signal, naming it and its true member count.
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
        warningA.Warning.LoadNames.ShouldBe(LoadsOfSignalA, ignoreOrder: true);
        warningB.Warning.LoadNames.ShouldBe(LoadsOfSignalB, ignoreOrder: true);
        warningA.WarningText.ShouldContain("A");
        warningA.WarningText.ShouldContain("4");
        // Issue #1011: the quantitative level report rides along — an ideal 1×4
        // split of the full input power hands each branch 0.25, which still
        // reaches the NAND threshold 0.125, so every branch reads "still a 1".
        warningA.SplitLine.ShouldContain("4");
        warningA.VerdictLines.Count.ShouldBe(warningA.Warning.LoadCount);
        warningA.Warning.Levels.DriverPowerOne.ShouldBe(1.0);
        warningA.Warning.Levels.BranchPower.ShouldBe(0.25);
        warningA.Warning.Levels.Branches.ShouldAllBe(b => b.ReadsAsOne && b.Threshold == 0.125);
    }

    [Fact]
    public async Task BuildNetwork_HalfAdder_ShowsGateDelaysAndCriticalPath()
    {
        // Issue #1002: every gate output shows its gate's propagation delay, and the
        // panel carries one critical-path line — the slowest gate chain sets how fast
        // the circuit can clock.
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        vm.Outputs.ShouldAllBe(
            o => o.DelayText.EndsWith("ps") && !o.DelayText.StartsWith("0.0") && !o.DelayText.StartsWith("0,0"),
            "every gate output shows its gate's non-zero propagation delay");
        vm.CriticalPathText.ShouldContain("ps");
        vm.CriticalPathText.Contains(" 0.0 ps ").ShouldBeFalse(
            "the half adder's critical path is non-zero");
        vm.CriticalPathText.Contains(" 0,0 ps ").ShouldBeFalse(
            "the half adder's critical path is non-zero");
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

using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Integration tests for the Logic panel's bus view (issue #1068, NAND game rung 5)
/// against the shipped <c>examples/Logic Gate 4-Bit Adder.lun</c>: the nine operand
/// toggles group into buses <c>A</c> and <c>B</c> with <c>Cin</c> staying a plain row,
/// the sum taps group into bus <c>S</c> with <c>Cout</c> plain. Setting A = 5 and B = 3
/// through the bus quick-set fields writes the member toggles and the panel reads
/// S = 8, Cout = 0 — and the canvas badges show the same bits as the panel outputs.
/// </summary>
public class LogicPanelBusViewTests : IClassFixture<LogicPanelBusViewTests.LoadedFourBitAdder>
{
    private readonly LoadedFourBitAdder _fixture;

    /// <summary>Attaches the shared loaded 4-bit-adder canvas.</summary>
    public LogicPanelBusViewTests(LoadedFourBitAdder fixture) => _fixture = fixture;

    [Fact]
    public async Task BuildNetwork_FourBitAdder_GroupsOperandAndSumSignalsIntoBuses()
    {
        var vm = await BuildPanel();

        var inputBuses = vm.InputRows.OfType<LogicSignalBusInputViewModel>().ToList();
        inputBuses.Select(b => b.Prefix).ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
        inputBuses.Single(b => b.Prefix == "A").Members.Select(m => m.PinName)
            .ShouldBe(new[] { "A0", "A1", "A2", "A3" });
        vm.InputRows.OfType<LogicNetworkInputViewModel>().Select(i => i.PinName)
            .ShouldBe(new[] { "Cin" }, "Cin has no indexed family and stays a plain toggle");

        var outputBuses = vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().ToList();
        outputBuses.Select(b => b.Prefix).ShouldBe(new[] { "S" });
        outputBuses.Single().Members.Select(m => m.PinName)
            .ShouldBe(new[] { "S0", "S1", "S2", "S3" });
        vm.OutputRows.OfType<LogicNetworkOutputViewModel>().ShouldContain(o => o.PinName == "Cout");
    }

    [Fact]
    public async Task QuickSet_BusFieldsFivePlusThree_ReadsSumEight_AndBadgesAgree()
    {
        var vm = await BuildPanel();
        var busA = vm.InputRows.OfType<LogicSignalBusInputViewModel>().Single(b => b.Prefix == "A");
        var busB = vm.InputRows.OfType<LogicSignalBusInputViewModel>().Single(b => b.Prefix == "B");

        busA.ValueText = "5";
        busB.ValueText = "3";

        busA.Members.Select(m => m.IsOn).ShouldBe(new[] { true, false, true, false },
            "A = 5 writes A0..A3 as 0101 (index 0 = LSB)");
        busB.Members.Select(m => m.IsOn).ShouldBe(new[] { true, true, false, false },
            "B = 3 writes B0..B3 as 0011");

        var busS = vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().Single(b => b.Prefix == "S");
        busS.DecimalValue.ShouldBe(8, "5 + 3 = 8 must read as one decimal number");
        busS.HeaderText.ShouldBe("S = 8 (1000)");
        vm.Outputs.Single(o => o.PinName == "Cout").IsOne.ShouldBeFalse();
        vm.Outputs.Single(o => o.PinName == "S3").IsOne.ShouldBeTrue();
        vm.Outputs.Single(o => o.PinName == "S0").IsOne.ShouldBeFalse();

        // Panel outputs and canvas badges agree: every sum tap's badge carries the
        // same bit the panel's output list shows (issue #994's overlay, unchanged).
        var badges = _fixture.Canvas.LogicGateStates.Badges;
        for (var stage = 0; stage < 4; stage++)
        {
            var badge = badges.Single(b => b.GroupName == $"T{stage}H2SUM" && b.PinName == "Y");
            badge.IsOne.ShouldBe(vm.Outputs.Single(o => o.PinName == $"S{stage}").IsOne,
                $"the S{stage} badge mirrors the panel output");
        }
        badges.Single(b => b.GroupName == "T3OROUT" && b.PinName == "Y").IsOne
            .ShouldBeFalse("the Cout badge mirrors the panel output");
    }

    /// <summary>Builds the panel VM over the fixture canvas and assembles its network.</summary>
    private async Task<LogicPanelViewModel> BuildPanel()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        return vm;
    }

    /// <summary>
    /// Shared fixture: loads the shipped 4-bit-adder example through the real load path
    /// once; every test assembles its own network from that canvas via the panel VM.
    /// </summary>
    public class LoadedFourBitAdder : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate 4-Bit Adder.lun";

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

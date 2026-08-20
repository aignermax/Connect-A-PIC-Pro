using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Counter 2-bit.lun</c> (issue
/// #1102, rung 5 — the datapath stone after the latch): five NAND readings of the
/// shipped NOT/NAND gate form a synchronous 2-bit counter. The low stage
/// <c>BIT0</c> is a toggle register (both inputs read its own committed output, so
/// each clock step commits <c>NAND(C0, C0) = NOT C0</c>); the three combinational
/// gates <c>XORH</c>/<c>XORA</c>/<c>XORB</c> form the classic three-NAND XOR whose
/// closing NAND is the second register <c>BIT1</c>, so each step commits
/// <c>C1' = C1 XOR C0</c> — the high bit toggles exactly when the low bit wraps.
/// One waveguide output feeds exactly one input (the canvas removes competing
/// connections on a pin — no free photonic fan-out), so every reused signal goes
/// through one of five combinational 1×2 splitter gates (<c>FANA</c>–<c>FANE</c>,
/// a 50/50 MMI reading Y1 = Y2 = A at half power). The file loads through the real
/// load path, the register designations ship persisted (#1093), assembly yields a
/// self-sufficient network (no inputs), and a sequence of
/// <see cref="LogicNetworkEvaluator.Step"/> calls counts C1C0 = 00 → 01 → 10 → 11 → 00.
/// </summary>
public class LogicGateCounter2BitExampleTests : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private const double NandThreshold = 0.125;

    private const string Bit0Tap = "C0";
    private const string Bit1Tap = "C1";
    private const int WireCount = 15;

    private static readonly string[] GateNames =
        { "BIT0", "FANA", "FANB", "FANC", "XORH", "FAND", "XORA", "XORB", "FANE", "BIT1" };
    private static readonly string[] RegisterGateNames = { "BIT0", "BIT1" };
    private static readonly string[] SplitterGateNames = { "FANA", "FANB", "FANC", "FAND", "FANE" };

    /// <summary>The persisted output signal names of the two counter bits.</summary>
    private static readonly Dictionary<string, string> ExpectedOutputSignalNames = new()
    {
        ["BIT0"] = Bit0Tap,
        ["BIT1"] = Bit1Tap,
    };

    private readonly CounterFixture _fixture;

    /// <summary>Attaches the shared counter fixture.</summary>
    public LogicGateCounter2BitExampleTests(CounterFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsTenGateGroups_WithBothToggleStagesDesignatedRegisters()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the counter contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(WireCount,
            "every reused signal fans out through a splitter — one waveguide output feeds one input");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            if (SplitterGateNames.Contains(group.GroupName))
            {
                roles.InputPinNames.ShouldBe(new[] { "A" });
                roles.OutputPinNames.ShouldBe(new[] { "Y1", "Y2" });
                roles.BiasPinNames.ShouldBeEmpty();
            }
            else
            {
                roles.InputPinNames.ShouldBe(new[] { "A", "B" });
                roles.OutputPinNames.ShouldBe(new[] { "Y" });
                roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            }
            roles.Threshold.ShouldBe(NandThreshold);
            roles.IsRegister.ShouldBe(RegisterGateNames.Contains(group.GroupName),
                $"only the two toggle stages are registers — '{group.GroupName}' (issue #1102)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain("0.125");
        }

        foreach (var registerName in RegisterGateNames)
        {
            var register = groups.Single(g => g.GroupName == registerName);
            register.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                new Dictionary<string, string> { ["Y"] = ExpectedOutputSignalNames[registerName] },
                $"register '{registerName}' ships its named counter-bit tap");
            register.Description.ShouldContain("register", Case.Sensitive,
                $"register '{registerName}' carries the register designation note");
        }
    }

    [Fact]
    public void AssembledNetwork_IsSelfSufficient_WithNamedCounterBitsAndTwoRegisters()
    {
        _fixture.Network.InputPinNames.ShouldBeEmpty(
            "every gate input is driven by the counter's own wiring — the clock step is the only stimulus");
        _fixture.Network.OutputPinNames.ShouldContain(Bit0Tap);
        _fixture.Network.OutputPinNames.ShouldContain(Bit1Tap);
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef("BIT0", "Y"), new LogicPinRef("BIT1", "Y") }, ignoreOrder: true,
            customMessage: "both toggle stages power up with their committed output cleared");
    }

    [Fact]
    public void StepSequence_CountsZeroToThree_AndWrapsBackToZero()
    {
        var network = _fixture.Network;
        var expectedCounts = new[] { 0, 1, 2, 3, 0, 1 };

        _fixture.ReadCount().ShouldBe(expectedCounts[0], "the counter powers up at 0");
        for (var step = 1; step < expectedCounts.Length; step++)
        {
            network.Step();
            _fixture.ReadCount().ShouldBe(expectedCounts[step],
                $"clock step {step} must count C1C0 = {Convert.ToString(expectedCounts[step], 2).PadLeft(2, '0')}");
        }
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameCounter()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);

            var reloadedGroups = LogicGateHalfAdderExampleTests.GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            foreach (var registerName in RegisterGateNames)
            {
                var register = reloadedGroups.Single(g => g.GroupName == registerName);
                register.TruthTablePinAssignment.ShouldNotBeNull();
                register.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
                    $"the register designation of '{registerName}' must survive the save → load round trip");
                register.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                    new Dictionary<string, string> { ["Y"] = ExpectedOutputSignalNames[registerName] },
                    $"the counter-bit tap of '{registerName}' must survive the save → load round trip");
            }
            reloadedCanvas.Connections.Count.ShouldBe(WireCount,
                "all counter wires must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames, ignoreOrder: true);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");

            reloaded.Evaluate(new Dictionary<string, bool>());
            reloaded.Step();
            reloaded.Step();
            var bits = reloaded.Evaluate(new Dictionary<string, bool>());
            (bits[Bit1Tap], bits[Bit0Tap]).ShouldBe((true, false),
                "two steps on the re-assembled counter must count to C1C0 = 10");
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class CounterFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Counter 2-bit.lun";

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Loads the shipped example, assembles its network, and settles it once.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
            Network = await LogicGateMuxExampleTests.AssembleNetwork(Canvas);
            Network.Evaluate(new Dictionary<string, bool>());
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>Reads the current count as the decimal value of C1C0.</summary>
        public int ReadCount()
        {
            var bits = Network.Evaluate(new Dictionary<string, bool>());
            return (bits["C1"] ? 2 : 0) + (bits["C0"] ? 1 : 0);
        }

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"counter-2bit-{Guid.NewGuid():N}.lun");
            var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(Canvas);
            var dialog = new Mock<IFileDialogService>();
            dialog.Setup(f => f.ShowSaveFileDialogAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(path);
            saveVm.FileDialogService = dialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
            File.Exists(path).ShouldBeTrue("the real save path must write the temp .lun");
            return path;
        }
    }
}

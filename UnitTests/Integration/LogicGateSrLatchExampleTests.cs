using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate SR-Latch.lun</c> (issue #1100,
/// rung 4/5 — the first sequential stone of the NAND game): two cross-coupled NAND
/// gates of the shipped NOT/NAND gate, both designated registers, wired to the
/// textbook active-low SR latch Q = NAND(S̄, Q̄), Q̄ = NAND(R̄, Q). The file loads
/// through the real load path, every group carries its persisted
/// <see cref="TruthTablePinAssignment"/> with the register designation, and the
/// merged <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into the
/// evaluable network: the set/reset toggles read <c>S̄</c> and <c>R̄</c> through the
/// persisted signal names (issue #1025), the taps read <c>Q</c> and <c>Q̄</c>, and the
/// set → hold → reset → hold sequence replays exactly the semantics pinned in
/// <c>LogicNetworkRegisterTests.Evaluate_SrLatchFromCrossCoupledNandRegisters_HoldsStateAcrossSteps</c>.
/// </summary>
public class LogicGateSrLatchExampleTests : IClassFixture<LogicGateSrLatchExampleTests.SrLatchFixture>
{
    private const double NandThreshold = 0.125;

    private const string SetSignal = "S̄";
    private const string ResetSignal = "R̄";
    private const string QTap = "Q";
    private const string QBarTap = "Q̄";

    private static readonly string[] GateNames = { "NANDQ", "NANDQB" };

    /// <summary>The persisted input signal names per gate group (issue #1025).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedInputSignalNames = new()
    {
        ["NANDQ"] = new() { ["A"] = SetSignal },
        ["NANDQB"] = new() { ["A"] = ResetSignal },
    };

    /// <summary>The persisted output signal names per gate group.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedOutputSignalNames = new()
    {
        ["NANDQ"] = new() { ["Y"] = QTap },
        ["NANDQB"] = new() { ["Y"] = QBarTap },
    };

    private readonly SrLatchFixture _fixture;

    /// <summary>Attaches the shared SR-latch fixture.</summary>
    public LogicGateSrLatchExampleTests(SrLatchFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachRegisterDesignatedWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the SR latch contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(2,
            "two cross-coupling wires join the two gates: Q → NANDQB.B and Q̄ → NANDQ.B");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            roles.InputPinNames.ShouldBe(new[] { "A", "B" });
            roles.OutputPinNames.ShouldBe(new[] { "Y" });
            roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            roles.Threshold.ShouldBe(NandThreshold);
            roles.IsRegister.ShouldBeTrue(
                $"group '{group.GroupName}' must ship the register designation (issue #1100)");
            roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            roles.OutputSignalNames.ShouldBe(ExpectedOutputSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its named output tap");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain("register", Case.Sensitive,
                "every gate carries the register designation note");
            group.Description.ShouldContain("0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesSetAndResetTogglesNamedOutputsAndRegisterState()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { ResetSignal, SetSignal }, ignoreOrder: true,
            customMessage: "the signal names merge the set/reset pins into one toggle each (issue #1025)");
        _fixture.Network.OutputPinNames.ShouldBe(new[] { QTap, QBarTap }, ignoreOrder: true,
            customMessage: "the named outputs replace the raw gate-pin taps");
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef("NANDQ", "Y"), new LogicPinRef("NANDQB", "Y") }, ignoreOrder: true,
            customMessage: "both gates power up with their committed output cleared");
    }

    [Fact]
    public void StepSequence_SetHoldResetHold_ReplaysThePinnedRegisterSemantics()
    {
        var network = _fixture.Network;

        // Set (active-low S̄): Q rises, Q̄ falls — and then holds while both
        // inputs rest at 1.
        network.Evaluate(_fixture.InputBits(set: false, reset: true));
        network.Step();
        network.Step();
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QTap].ShouldBeTrue("the latch is set");
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QBarTap].ShouldBeFalse();

        network.Step();
        network.Step();
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QTap].ShouldBeTrue(
            "resting inputs hold the set state");

        // Reset (active-low R̄): Q falls, Q̄ rises — and holds again.
        network.Evaluate(_fixture.InputBits(set: true, reset: false));
        network.Step();
        network.Step();
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QTap].ShouldBeFalse("the latch is reset");
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QBarTap].ShouldBeTrue();

        network.Step();
        network.Step();
        network.Evaluate(_fixture.InputBits(set: true, reset: true))[QTap].ShouldBeFalse(
            "resting inputs hold the reset state");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameLatch()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);

            var reloadedGroups = LogicGateHalfAdderExampleTests.GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            reloadedGroups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
                "the persisted pin roles must survive the save → load round trip");
            foreach (var group in reloadedGroups)
            {
                group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
                    $"the register designation of '{group.GroupName}' must survive the save → load round trip");
                group.TruthTablePinAssignment!.InputSignalNames.ShouldBe(
                    ExpectedInputSignalNames[group.GroupName],
                    $"the input signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
                group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                    ExpectedOutputSignalNames[group.GroupName],
                    $"the output signal names of '{group.GroupName}' must survive the save → load round trip");
            }
            reloadedCanvas.Connections.Count.ShouldBe(2,
                "both cross-coupling wires must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");
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
    public class SrLatchFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate SR-Latch.lun";

        /// <summary>Laser wavelength the persisted roles were extracted at.</summary>
        public const int WavelengthNm = 1550;

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Loads the shipped example and assembles its logic network.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
            Network = await LogicGateMuxExampleTests.AssembleNetwork(Canvas);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>The network input bits for one set/reset pair — one bit per signal.</summary>
        public Dictionary<string, bool> InputBits(bool set, bool reset) =>
            new() { [SetSignal] = set, [ResetSignal] = reset };

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"sr-latch-{Guid.NewGuid():N}.lun");
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

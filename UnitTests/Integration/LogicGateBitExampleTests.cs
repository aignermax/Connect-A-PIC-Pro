using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Bit.lun</c> (issue
/// #1119, rung 5 of the NAND game — the NAND2TETRIS Bit between the SR latch
/// and the counter): the textbook load-enabled register bit D = MUX(Load, Q, Din)
/// in front of a register. The combinational front-end reads exactly like the
/// shipped MUX (issue #1059): NOT1 inverts Load, NANDA is the hold arm
/// NAND(Q, NOT(Load)), NANDB is the load arm NAND(Din, Load), and Out closes the
/// multiplexer as NAND(NANDA.Y, NANDB.Y) = (Q AND NOT(Load)) OR (Din AND Load).
/// Out is designated a register (issue #1098), so while the network settles its
/// output pin keeps reading the last committed Q — the feedback wire Out.Y →
/// NANDA.A forms no combinational loop — and only
/// <see cref="LogicNetworkEvaluator.Step"/> samples the settled D and commits it
/// as the new Q (D-semantics). Every waveguide serves exactly one output pin and
/// one input pin; the only fan-out (Load onto NOT1.A and NANDB.A) happens at the
/// logic layer, where the persisted signal names (issue #1025) merge the two
/// unconnected pins into the single network toggle <c>Load</c> — exactly the two
/// toggles <c>Din</c> and <c>Load</c>, one named output <c>Q</c>. The file loads
/// through the real load path, every group carries its persisted
/// <see cref="TruthTablePinAssignment"/>, and the merged
/// <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into the
/// evaluable network the sequence below pins.
/// </summary>
public class LogicGateBitExampleTests
    : IClassFixture<LogicGateBitExampleTests.BitFixture>
{
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;

    private const string DinSignal = "Din";
    private const string LoadSignal = "Load";
    private const string QTap = "Q";
    private const string RegisterName = "Out";

    private static readonly string[] GateNames = { "NOT1", "NANDA", "NANDB", "Out" };

    /// <summary>The persisted input signal names per gate group (issue #1025); null = no named pins.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedInputSignalNames = new()
    {
        ["NOT1"] = new() { ["A"] = LoadSignal },
        ["NANDA"] = null,
        ["NANDB"] = new() { ["A"] = LoadSignal, ["B"] = DinSignal },
        ["Out"] = null,
    };

    private readonly BitFixture _fixture;

    /// <summary>Attaches the shared bit fixture.</summary>
    public LogicGateBitExampleTests(BitFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_WithPersistedRolesAndTheRegisterDesignation()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the load-enabled bit contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(4,
            "four wires join the four gates: the two MUX arms into Out, the select inversion, " +
            "and the register feedback Out.Y → NANDA.A — every waveguide keeps one driver and one load");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            var isNot = group.GroupName == "NOT1";
            roles.InputPinNames.ShouldBe(isNot ? new[] { "A" } : new[] { "A", "B" });
            roles.OutputPinNames.ShouldBe(new[] { "Y" });
            roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            roles.Threshold.ShouldBe(isNot ? NotThreshold : NandThreshold);
            roles.IsRegister.ShouldBe(group.GroupName == RegisterName,
                $"the register designation of '{group.GroupName}' (issue #1098) — only Out stores the bit");
            roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }

        _fixture.Groups.Single(g => g.GroupName == RegisterName).TruthTablePinAssignment!
            .OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["Y"] = QTap },
                "the register ships the named output tap Q (issue #1025)");
        _fixture.Groups.Single(g => g.GroupName == RegisterName).Description
            .ShouldContain("register", Case.Sensitive,
                "the register carries the designation note");
    }

    [Fact]
    public void AssembledNetwork_ExposesDinAndLoadTogglesTheNamedQAndTheRegisterState()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { DinSignal, LoadSignal }, ignoreOrder: true,
            customMessage: "the signal names merge the three unconnected operand pins into exactly " +
                "two network inputs (issue #1025) — Load drives NOT1.A and NANDB.A without a wire");
        _fixture.Network.OutputPinNames.ShouldBe(
            new[] { QTap, "NOT1.Y", "NANDA.Y", "NANDB.Y" }, ignoreOrder: true,
            customMessage: "the named tap Q replaces the register's raw gate-pin name; " +
                "the combinational gates stay readable as raw taps");
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef(RegisterName, "Y") }, ignoreOrder: true,
            customMessage: "the bit powers up as one state element, committed output cleared");
    }

    [Fact]
    public void StepSequence_LoadStoreHoldAndLoadZero_ReplaysTheBitSemantics()
    {
        AssertLoadStoreHoldAndLoadZero(_fixture.Network);
    }

    /// <summary>
    /// Asserts the pinned bit sequence against one assembled network: power-up
    /// cleared, then Load=1 stores Din=1, Load=0 holds the 1 across two steps
    /// while Din rests low, and Load=1 stores Din=0.
    /// </summary>
    private static void AssertLoadStoreHoldAndLoadZero(LogicNetworkEvaluator network)
    {
        network.Evaluate(Bits(load: true, din: true))[QTap].ShouldBeFalse(
            "powered up cleared: Q reads the committed 0, not the combinational D");

        network.Step();
        var stored = network.Evaluate(Bits(load: true, din: true));
        stored[QTap].ShouldBeTrue("Load=1: the step stores Din=1");
        stored["NOT1.Y"].ShouldBeFalse("the select inversion stays readable as a tap");
        stored["NANDB.Y"].ShouldBeFalse("the load arm passes Din while Load is high");

        network.Evaluate(Bits(load: false, din: false));
        network.Step();
        network.Step();
        var held = network.Evaluate(Bits(load: false, din: false));
        held[QTap].ShouldBeTrue(
            "Load=0: the bit holds its stored 1 across two steps while Din rests low");
        held["NANDA.Y"].ShouldBeFalse("the hold arm passes the committed Q while Load is low");
        held["NANDB.Y"].ShouldBeTrue("the load arm blocks Din while Load is low");

        network.Evaluate(Bits(load: true, din: false));
        network.Step();
        network.Evaluate(Bits(load: true, din: false))[QTap].ShouldBeFalse(
            "Load=1: the step stores Din=0");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameBit()
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
                var roles = group.TruthTablePinAssignment!;
                roles.IsRegister.ShouldBe(group.GroupName == RegisterName,
                    $"the register designation of '{group.GroupName}' must survive the save → load round trip");
                roles.InputPinNames.ToArray().ShouldBe(
                    group.GroupName == "NOT1" ? new[] { "A" } : new[] { "A", "B" },
                    customMessage: $"the input roles of '{group.GroupName}' must survive the save → load round trip");
                roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames[group.GroupName],
                    $"the input signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
            }
            reloadedGroups.Single(g => g.GroupName == RegisterName).TruthTablePinAssignment!
                .OutputSignalNames.ShouldBe(new Dictionary<string, string> { ["Y"] = QTap },
                    "the named output tap Q must survive the save → load round trip (#1025)");
            reloadedCanvas.Connections.Count.ShouldBe(4,
                "every bit wire — the feedback included — must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state element");

            AssertLoadStoreHoldAndLoadZero(reloaded);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>The network input bits for one Load/Din pair — one bit per signal (issue #1025).</summary>
    private static Dictionary<string, bool> Bits(bool load, bool din) =>
        new() { [DinSignal] = din, [LoadSignal] = load };

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class BitFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Bit.lun";

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

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bit-{Guid.NewGuid():N}.lun");
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

using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Register 2-bit.lun</c> (issue
/// #1133, rung 5 of the NAND game — the NAND2TETRIS Register slice, the missing
/// middle rung between the load-enabled Bit #1119 and the PC #1128): two load-enabled
/// Bits — each exactly the shipped Bit pattern, a MUX in front of a register — sharing
/// one LOAD control. Per bit <c>D_i = (R_i AND NOT(LOAD)) OR (D_i AND LOAD)</c>: the
/// shared NOTL inverts the control input <c>LOAD</c>, H_i = NAND(R_i, NL) is the hold
/// arm, LD_i = NAND(D_i, LOAD) the load arm, and the register REG_i closes the
/// multiplexer as NAND(H_i, LD_i) with the named output tap R_i. Because every
/// register samples the same settled pre-step state and commits simultaneously
/// (D-semantics), each <see cref="LogicNetworkEvaluator.Step"/> is one full clock
/// tick: LOAD = 1 with D1D0 = 10 commits R1R0 = 10 at the next clock, and LOAD = 0
/// holds the word across further steps. Every waveguide serves exactly one output pin
/// and one input pin: the only fan-out of a wire-carried signal is the inverted
/// select NL onto the two hold arms, which the copy gate CPNL serves (one waveguide
/// carries exactly one signal, each splitter arm takes half the power, comfortably
/// above threshold); the committed bits R_i feed only their own hold arm, and the
/// fan-out of LOAD onto NOTL/LD0/LD1 plus the single consumers D0/D1 need no wire at
/// all — the persisted signal names (issue #1025) merge the unconnected pins into
/// the three network toggles <c>LOAD</c>, <c>D0</c>, <c>D1</c>. The file loads
/// through the real load path, every group carries its persisted
/// <see cref="TruthTablePinAssignment"/>, and the merged
/// <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into the
/// evaluable network the sequences below pin.
/// </summary>
public class LogicGateRegister2BitExampleTests
    : IClassFixture<LogicGateRegister2BitExampleTests.RegisterFixture>
{
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;

    private const string LoadSignal = "LOAD";
    private const string LowDataSignal = "D0";
    private const string HighDataSignal = "D1";
    private const string LowBitTap = "R0";
    private const string HighBitTap = "R1";

    private static readonly string[] GateNames =
        { "NOTL", "CPNL", "H0", "LD0", "REG0", "H1", "LD1", "REG1" };
    private static readonly string[] RegisterNames = { "REG0", "REG1" };
    private static readonly string[] CopyNames = { "CPNL" };
    private static readonly string[] NotNames = { "NOTL" };

    /// <summary>The persisted input signal names of the groups with named unconnected pins (issue #1025).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedInputSignalNames = new()
    {
        ["NOTL"] = new() { ["A"] = LoadSignal },
        ["LD0"] = new() { ["A"] = LowDataSignal, ["B"] = LoadSignal },
        ["LD1"] = new() { ["A"] = HighDataSignal, ["B"] = LoadSignal },
    };

    /// <summary>The persisted output signal names per gate group — the stored-word taps.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedOutputSignalNames = new()
    {
        ["REG0"] = new() { ["Y"] = LowBitTap },
        ["REG1"] = new() { ["Y"] = HighBitTap },
    };

    private readonly RegisterFixture _fixture;

    /// <summary>Attaches the shared register fixture.</summary>
    public LogicGateRegister2BitExampleTests(RegisterFixture fixture) => _fixture = fixture;

    /// <summary>The resting input bits: LOAD low (hold), the data inputs low.</summary>
    private static Dictionary<string, bool> HoldingInputBits() => Bits(load: false, d0: false, d1: false);

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_WithPersistedRolesAndRegisterDesignations()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the 2-bit register contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(9,
            "nine wires join the eight gates: the select inversion through the copy stage onto " +
            "both hold arms, the two MUX arms into each register, and the two register feedbacks " +
            "— every waveguide keeps one driver and one load");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            if (CopyNames.Contains(group.GroupName))
            {
                roles.InputPinNames.ShouldBe(new[] { "A" });
                roles.OutputPinNames.ShouldBe(new[] { "Y1", "Y2" });
                roles.BiasPinNames.ShouldBeEmpty();
                roles.Threshold.ShouldBe(NotThreshold);
                roles.IsRegister.ShouldBeFalse("a copy gate is combinational");
            }
            else if (NotNames.Contains(group.GroupName))
            {
                roles.InputPinNames.ShouldBe(new[] { "A" });
                roles.OutputPinNames.ShouldBe(new[] { "Y" });
                roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
                roles.Threshold.ShouldBe(NotThreshold);
                roles.IsRegister.ShouldBeFalse("the select inverter is combinational");
            }
            else
            {
                roles.InputPinNames.ShouldBe(new[] { "A", "B" });
                roles.OutputPinNames.ShouldBe(new[] { "Y" });
                roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
                roles.Threshold.ShouldBe(NandThreshold);
                roles.IsRegister.ShouldBe(RegisterNames.Contains(group.GroupName),
                    $"the register designation of '{group.GroupName}' (issue #1098)");
            }
            roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames.GetValueOrDefault(group.GroupName),
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            var expectedOutputs = ExpectedOutputSignalNames.GetValueOrDefault(group.GroupName);
            roles.OutputSignalNames.ShouldBe(expectedOutputs,
                $"group '{group.GroupName}' ships its output tap identity (issue #1025)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(
                CopyNames.Contains(group.GroupName) || NotNames.Contains(group.GroupName)
                    ? "0.375" : "0.125");
        }

        foreach (var register in RegisterNames)
        {
            _fixture.Groups.Single(g => g.GroupName == register).Description
                .ShouldContain("register", Case.Sensitive,
                    "every register carries the designation note");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesLoadAndDataTogglesTheNamedTapsAndTwoRegisters()
    {
        _fixture.Network.InputPinNames.ShouldBe(
            new[] { LoadSignal, LowDataSignal, HighDataSignal }, ignoreOrder: true,
            customMessage: "the signal names merge the five unconnected operand pins into exactly " +
                "three network inputs (issue #1025) — LOAD drives NOTL.A, LD0.B and LD1.B without a wire");
        _fixture.Network.OutputPinNames.ShouldBe(
            new[] { LowBitTap, HighBitTap }
                .Concat(new[] { "NOTL", "H0", "LD0", "H1", "LD1" }.Select(name => $"{name}.Y"))
                .Concat(CopyNames.SelectMany(name => new[] { $"{name}.Y1", $"{name}.Y2" })),
            ignoreOrder: true,
            customMessage: "the two named taps replace the raw register pin names; " +
                "the combinational gates and the copy gate stay readable as raw taps");
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef("REG0", "Y"), new LogicPinRef("REG1", "Y") }, ignoreOrder: true,
            customMessage: "both bits power up as state elements, committed outputs cleared");
    }

    [Fact]
    public async Task BusView_GroupsLoadAndWordBitsIntoDecimalRows_StartingAtZero()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        var inputBuses = vm.InputRows.OfType<LogicSignalBusInputViewModel>().ToList();
        inputBuses.Select(b => b.Prefix).ShouldBe(new[] { "D" },
            "D0 and D1 group into the data bus shown as one decimal row (issue #1068)");
        inputBuses.Single().Members.Select(m => m.PinName).ShouldBe(new[] { "D0", "D1" });
        vm.InputRows.OfType<LogicNetworkInputViewModel>().Select(i => i.PinName)
            .ShouldBe(new[] { LoadSignal }, "LOAD has no indexed family and stays a plain toggle");

        var outputBuses = vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().ToList();
        outputBuses.Select(b => b.Prefix).ShouldBe(new[] { "R" },
            "R0 and R1 group into the register bus shown as one decimal row (issue #1068)");
        var busR = outputBuses.Single();
        busR.Members.Select(m => m.PinName).ShouldBe(new[] { "R0", "R1" });
        busR.DecimalValue.ShouldBe(0, "the register powers up cleared: the bus row R reads 0");
    }

    [Fact]
    public void StepSequence_LoadHigh_CommitsTheExternalWord()
    {
        AssertLoadTwo(_fixture.Network);
    }

    [Fact]
    public void StepSequence_LoadLow_HoldsTheWordAcrossSteps()
    {
        AssertHoldAcrossSteps(_fixture.Network);
    }

    /// <summary>
    /// Drives the network back into the powered-up-cleared state: LOAD = 1 with
    /// D1D0 = 00 commits R = 0 at the next clock. Fact execution order is
    /// unspecified, so every sequence pins its own starting state.
    /// </summary>
    private static void ResetToZero(LogicNetworkEvaluator network)
    {
        network.Evaluate(Bits(load: true, d0: false, d1: false));
        network.Step();
        var cleared = network.Evaluate(HoldingInputBits());
        cleared[HighBitTap].ShouldBeFalse();
        cleared[LowBitTap].ShouldBeFalse("after the clear commit the register reads R = 0");
    }

    /// <summary>
    /// Asserts the pinned load sequence against one assembled network: from R = 0,
    /// LOAD = 1 with D1D0 = 10 commits R = 2 at the next clock.
    /// </summary>
    private static void AssertLoadTwo(LogicNetworkEvaluator network)
    {
        ResetToZero(network);

        var beforeLoad = network.Evaluate(Bits(load: true, d0: false, d1: true));
        beforeLoad[HighBitTap].ShouldBeFalse();
        beforeLoad[LowBitTap].ShouldBeFalse(
            "cleared: R reads the committed 0, not the pending load value");
        beforeLoad["LD0.Y"].ShouldBeTrue("the load arm blocks D0 = 0");
        beforeLoad["LD1.Y"].ShouldBeFalse("the load arm passes D1 = 1 while LOAD is high");
        beforeLoad["H0.Y"].ShouldBeTrue("the hold arm is blocked while LOAD is high");

        network.Step();
        var loaded = network.Evaluate(Bits(load: true, d0: false, d1: true));
        loaded[HighBitTap].ShouldBeTrue();
        loaded[LowBitTap].ShouldBeFalse("LOAD=1: the step commits D1D0 = 10 — R = 2");
    }

    /// <summary>
    /// Asserts the pinned hold sequence: stores R = 2, then with LOAD = 0 the word
    /// stays committed across two further clock steps while the data inputs rest low.
    /// </summary>
    private static void AssertHoldAcrossSteps(LogicNetworkEvaluator network)
    {
        AssertLoadTwo(network);

        network.Evaluate(HoldingInputBits());
        network.Step();
        network.Step();
        var held = network.Evaluate(HoldingInputBits());
        held[HighBitTap].ShouldBeTrue();
        held[LowBitTap].ShouldBeFalse(
            "LOAD=0: the register holds R = 2 across two steps while the data inputs rest low");
        held["NOTL.Y"].ShouldBeTrue("the inverted select feeds the hold arms while LOAD is low");
        held["H0.Y"].ShouldBeTrue("the hold arm blocks the committed R0 = 0");
        held["H1.Y"].ShouldBeFalse("the hold arm passes the committed R1 = 1");
        held["LD0.Y"].ShouldBeTrue("the load arm blocks D0 while LOAD is low");
        held["LD1.Y"].ShouldBeTrue("the load arm blocks D1 while LOAD is low");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameRegister()
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
                roles.IsRegister.ShouldBe(RegisterNames.Contains(group.GroupName),
                    $"the register designation of '{group.GroupName}' must survive the save → load round trip");
                var expectedInputs = CopyNames.Contains(group.GroupName) || NotNames.Contains(group.GroupName)
                    ? new[] { "A" } : new[] { "A", "B" };
                roles.InputPinNames.ToArray().ShouldBe(expectedInputs,
                    customMessage: $"the input roles of '{group.GroupName}' must survive the save → load round trip");
                roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames.GetValueOrDefault(group.GroupName),
                    $"the input signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
                roles.OutputSignalNames.ShouldBe(ExpectedOutputSignalNames.GetValueOrDefault(group.GroupName),
                    $"the output signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
            }
            reloadedCanvas.Connections.Count.ShouldBe(9,
                "every register wire — the feedbacks included — must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");

            AssertLoadTwo(reloaded);
            AssertHoldAcrossSteps(reloaded);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>The network input bits for one LOAD/D0/D1 triple — one bit per signal (issue #1025).</summary>
    private static Dictionary<string, bool> Bits(bool load, bool d0, bool d1) =>
        new() { [LoadSignal] = load, [LowDataSignal] = d0, [HighDataSignal] = d1 };

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class RegisterFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Register 2-bit.lun";

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
            var path = Path.Combine(Path.GetTempPath(), $"register-2bit-{Guid.NewGuid():N}.lun");
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

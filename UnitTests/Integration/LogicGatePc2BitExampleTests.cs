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
/// Pinned tests for the shipped <c>examples/Logic Gate PC 2-bit.lun</c> (issue
/// #1128, rung 5 of the NAND game — the NAND2TETRIS PC slice, the composition of
/// the shipped 2-bit counter #1102 and the load-enabled Bit #1119): a 2-bit
/// counter whose every bit is the shipped Bit pattern — a MUX in front of a
/// register. Per bit <c>D_i = (INC_i AND NOT(LOAD)) OR (L_i AND LOAD)</c>: the
/// shared NOTL inverts the control input <c>LOAD</c>, H_i = NAND(INC_i, NL) is the
/// count arm, LD_i = NAND(L_i, LOAD) the load arm, and the register REG_i closes
/// the multiplexer as NAND(H_i, LD_i) with the named output tap C_i. The increment
/// reuses the counter's wiring idioms: N0 inverts the committed C0 (INC0 = NOT(C0),
/// the low bit toggles every clock) and the 4-NAND XOR X = NAND(C0, C1),
/// R = NAND(C0, X), S = NAND(C1, X), INC = NAND(R, S) yields INC1 = C0 ⊕ C1, so
/// the high bit toggles exactly when the low bit wraps. Because every register
/// samples the same settled pre-step state and commits simultaneously
/// (D-semantics), each <see cref="LogicNetworkEvaluator.Step"/> is one full clock
/// tick: LOAD = 0 counts C1C0 = 00 → 01 → 10 → 11 → 00, LOAD = 1 commits the
/// external L1L0 instead. Every waveguide serves exactly one output pin and one
/// input pin: the committed bits, the XOR pivot X and the inverted select NL fan
/// out through the copy gates CP0/CP0X/CP1/CPX/CPNL (one waveguide carries exactly
/// one signal, each splitter arm takes half the power, comfortably above
/// threshold), while the fan-out of LOAD onto NOTL/LD0/LD1 and the single
/// consumers L0/L1 need no wire at all — the persisted signal names (issue #1025)
/// merge the unconnected pins into the three network toggles <c>LOAD</c>,
/// <c>L0</c>, <c>L1</c>. The file loads through the real load path, every group
/// carries its persisted <see cref="TruthTablePinAssignment"/>, and the merged
/// <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into the
/// evaluable network the sequences below pin.
/// </summary>
public class LogicGatePc2BitExampleTests
    : IClassFixture<LogicGatePc2BitExampleTests.PcFixture>
{
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;

    private const string LoadSignal = "LOAD";
    private const string LowLoadSignal = "L0";
    private const string HighLoadSignal = "L1";
    private const string LowBitTap = "C0";
    private const string HighBitTap = "C1";

    private static readonly string[] GateNames =
    {
        "NOTL", "N0", "X", "R", "S", "INC", "H0", "LD0", "REG0", "H1", "LD1", "REG1",
        "CP0", "CP0X", "CP1", "CPX", "CPNL",
    };
    private static readonly string[] RegisterNames = { "REG0", "REG1" };
    private static readonly string[] CopyNames = { "CP0", "CP0X", "CP1", "CPX", "CPNL" };
    private static readonly string[] NotNames = { "NOTL", "N0" };

    /// <summary>The persisted input signal names of the groups with named unconnected pins (issue #1025).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedInputSignalNames = new()
    {
        ["NOTL"] = new() { ["A"] = LoadSignal },
        ["LD0"] = new() { ["A"] = LowLoadSignal, ["B"] = LoadSignal },
        ["LD1"] = new() { ["A"] = HighLoadSignal, ["B"] = LoadSignal },
    };

    /// <summary>The persisted output signal names per gate group — the count taps and the readable XOR stages.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedOutputSignalNames = new()
    {
        ["X"] = new() { ["Y"] = "X" },
        ["R"] = new() { ["Y"] = "R" },
        ["S"] = new() { ["Y"] = "S" },
        ["REG0"] = new() { ["Y"] = LowBitTap },
        ["REG1"] = new() { ["Y"] = HighBitTap },
    };

    private readonly PcFixture _fixture;

    /// <summary>Attaches the shared PC fixture.</summary>
    public LogicGatePc2BitExampleTests(PcFixture fixture) => _fixture = fixture;

    /// <summary>The resting input bits: LOAD low (count), the external load bits low.</summary>
    private static Dictionary<string, bool> CountingInputBits() => Bits(load: false, l0: false, l1: false);

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_WithPersistedRolesAndRegisterDesignations()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the 2-bit program counter contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(22,
            "twenty-two wires join the seventeen gates: the register feedback through the copy " +
            "stages, the XOR chain, the select inversion, and the four MUX arms into the two " +
            "registers — every waveguide keeps one driver and one load");

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
                roles.IsRegister.ShouldBeFalse("the inverters are combinational");
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
            new[] { LoadSignal, LowLoadSignal, HighLoadSignal }, ignoreOrder: true,
            customMessage: "the signal names merge the five unconnected operand pins into exactly " +
                "three network inputs (issue #1025) — LOAD drives NOTL.A, LD0.B and LD1.B without a wire");
        _fixture.Network.OutputPinNames.ShouldBe(
            new[] { LowBitTap, HighBitTap, "X", "R", "S" }
                .Concat(new[] { "NOTL", "N0", "INC", "H0", "LD0", "H1", "LD1" }.Select(name => $"{name}.Y"))
                .Concat(CopyNames.SelectMany(name => new[] { $"{name}.Y1", $"{name}.Y2" })),
            ignoreOrder: true,
            customMessage: "five named taps replace the raw gate-pin names; " +
                "the combinational gates and copy gates stay readable as raw taps");
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef("REG0", "Y"), new LogicPinRef("REG1", "Y") }, ignoreOrder: true,
            customMessage: "both bits power up as state elements, committed outputs cleared");
    }

    [Fact]
    public async Task BusView_GroupsLoadAndCountBitsIntoDecimalRows_StartingAtZero()
    {
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);
        await vm.BuildNetworkCommand.ExecuteAsync(null);
        vm.HasNetwork.ShouldBeTrue(vm.StatusText);

        var inputBuses = vm.InputRows.OfType<LogicSignalBusInputViewModel>().ToList();
        inputBuses.Select(b => b.Prefix).ShouldBe(new[] { "L" },
            "L0 and L1 group into the load bus shown as one decimal row (issue #1068)");
        inputBuses.Single().Members.Select(m => m.PinName).ShouldBe(new[] { "L0", "L1" });
        vm.InputRows.OfType<LogicNetworkInputViewModel>().Select(i => i.PinName)
            .ShouldBe(new[] { LoadSignal }, "LOAD has no indexed family and stays a plain toggle");

        var outputBuses = vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().ToList();
        outputBuses.Select(b => b.Prefix).ShouldBe(new[] { "C" },
            "C0 and C1 group into the count bus shown as one decimal row (issue #1068)");
        var busC = outputBuses.Single();
        busC.Members.Select(m => m.PinName).ShouldBe(new[] { "C0", "C1" });
        busC.DecimalValue.ShouldBe(0, "the PC powers up cleared: the bus row C reads 0");
    }

    [Fact]
    public void StepSequence_LoadLow_CountsZeroToThreeAndWraps()
    {
        AssertCountsZeroToThreeAndWraps(_fixture.Network);
    }

    [Fact]
    public void StepSequence_LoadHigh_CommitsTheExternalValue_ThenCountsOn()
    {
        AssertLoadTwoThenCountOn(_fixture.Network);
    }

    /// <summary>
    /// Asserts the pinned counting sequence against one assembled network: power-up
    /// at C1C0 = 00, then 00 → 01 → 10 → 11 → 00 over four clock steps with LOAD low.
    /// </summary>
    private static void AssertCountsZeroToThreeAndWraps(LogicNetworkEvaluator network)
    {
        var poweredUp = network.Evaluate(CountingInputBits());
        poweredUp[HighBitTap].ShouldBeFalse();
        poweredUp[LowBitTap].ShouldBeFalse("powered up cleared: C1C0 = 00");

        foreach (var (expectedC1, expectedC0) in new[]
                 {
                     (false, true),   // 01 — the low bit toggled
                     (true, false),   // 10 — C0 wrapped 1 → 0, the high bit toggled
                     (true, true),    // 11
                     (false, false),  // 00 — both wrapped
                 })
        {
            network.Step();
            var settled = network.Evaluate(CountingInputBits());
            settled[HighBitTap].ShouldBe(expectedC1, "after the step C1C0 must read the next count");
            settled[LowBitTap].ShouldBe(expectedC0, "after the step C1C0 must read the next count");
            settled["X"].ShouldBe(!(expectedC0 && expectedC1), "the XOR pivot stays readable as a tap");
            settled["R"].ShouldBe(!(expectedC0 && !(expectedC0 && expectedC1)), "the C0 arm stays readable");
            settled["S"].ShouldBe(!(expectedC1 && !(expectedC0 && expectedC1)), "the C1 arm stays readable");
        }
    }

    /// <summary>
    /// Asserts the pinned load sequence against one assembled network: powered up
    /// cleared, LOAD = 1 with L1L0 = 10 commits C = 2 at the next clock, and with
    /// LOAD back to 0 the following clock counts on to 3.
    /// </summary>
    private static void AssertLoadTwoThenCountOn(LogicNetworkEvaluator network)
    {
        var beforeLoad = network.Evaluate(Bits(load: true, l0: false, l1: true));
        beforeLoad[HighBitTap].ShouldBeFalse();
        beforeLoad[LowBitTap].ShouldBeFalse(
            "powered up cleared: C reads the committed 0, not the pending load value");
        beforeLoad["LD0.Y"].ShouldBeTrue("the load arm blocks L0 = 0");
        beforeLoad["LD1.Y"].ShouldBeFalse("the load arm passes L1 = 1 while LOAD is high");
        beforeLoad["H0.Y"].ShouldBeTrue("the count arm is blocked while LOAD is high");

        network.Step();
        var loaded = network.Evaluate(Bits(load: true, l0: false, l1: true));
        loaded[HighBitTap].ShouldBeTrue();
        loaded[LowBitTap].ShouldBeFalse("LOAD=1: the step commits L1L0 = 10 — C = 2");

        network.Evaluate(CountingInputBits());
        network.Step();
        var countedOn = network.Evaluate(CountingInputBits());
        countedOn[HighBitTap].ShouldBeTrue();
        countedOn[LowBitTap].ShouldBeTrue("LOAD back to 0: the next step counts on — C = 3");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSamePc()
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
            reloadedCanvas.Connections.Count.ShouldBe(22,
                "every PC wire — the register feedback included — must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");

            AssertCountsZeroToThreeAndWraps(reloaded);
            AssertLoadTwoThenCountOn(reloaded);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>The network input bits for one LOAD/L0/L1 triple — one bit per signal (issue #1025).</summary>
    private static Dictionary<string, bool> Bits(bool load, bool l0, bool l1) =>
        new() { [LoadSignal] = load, [LowLoadSignal] = l0, [HighLoadSignal] = l1 };

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class PcFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate PC 2-bit.lun";

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
            var path = Path.Combine(Path.GetTempPath(), $"pc-2bit-{Guid.NewGuid():N}.lun");
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

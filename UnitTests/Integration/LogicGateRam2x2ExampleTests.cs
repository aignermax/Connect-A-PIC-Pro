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
/// Pinned tests for the shipped <c>examples/Logic Gate RAM 2x2.lun</c> (issue #1142,
/// rung 5 of the NAND game — the NAND2TETRIS 'RAM' slice between the Register #1138
/// and the PC-with-program): two words x two bits behind one address bit. The write
/// path demultiplexes LOAD by ADDR (the AND/NOT patterns behind the MUX example #1059):
/// <c>EN_w = NAND(select_w, LOAD)</c> is the inverted word enable, <c>IW_w = NOT(EN_w)</c>
/// the true one; each word is the shipped 2-bit Register pattern — hold arm
/// <c>H = NAND(R, EN)</c>, load arm <c>LE = NAND(D, IW)</c>, register
/// <c>REG = NAND(H, LE)</c> — with a read-tap copy feeding both the register's own
/// hold feedback and the read MUX arm. The read path is the shipped 2-to-1 MUX
/// (#1059) per data bit: <c>MA = NAND(word 0, NOT(ADDR))</c>,
/// <c>MB = NAND(word 1, ADDR)</c>, <c>OUT = NAND(MA, MB)</c>. Fan-outs of
/// select/enable levels are served by copy cascades (the register's CPNL pattern) —
/// every waveguide carries exactly one signal; the persisted signal names (issue
/// #1025) merge the unconnected pins into the four toggles <c>ADDR</c>,
/// <c>LOAD</c>, <c>D0</c>, <c>D1</c>. Because every register samples the same settled
/// pre-step state and commits simultaneously (D-semantics), each
/// <see cref="LogicNetworkEvaluator.Step"/> is one full clock tick: ADDR selects which
/// word stores under LOAD and which word answers on the read bus R.
/// </summary>
public class LogicGateRam2x2ExampleTests
    : IClassFixture<LogicGateRam2x2ExampleTests.RamFixture>
{
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;

    private const string AddressSignal = "ADDR";
    private const string LoadSignal = "LOAD";
    private const string LowDataSignal = "D0";
    private const string HighDataSignal = "D1";
    private const string LowReadTap = "R0";
    private const string HighReadTap = "R1";

    private static readonly string[] GateNames =
    {
        "CPAX", "CPA", "NOTA", "CPNX", "CPN",
        "EN0", "CPEA0", "CPEB0", "IW0", "CPI0",
        "EN1", "CPEA1", "CPEB1", "IW1", "CPI1",
        "H00", "LE00", "REG00", "CP00",
        "H01", "LE01", "REG01", "CP01",
        "H10", "LE10", "REG10", "CP10",
        "H11", "LE11", "REG11", "CP11",
        "MA0", "MB0", "OUT0", "MA1", "MB1", "OUT1",
    };
    private static readonly string[] RegisterNames = { "REG00", "REG01", "REG10", "REG11" };
    private static readonly string[] CopyNames =
    {
        "CPAX", "CPA", "CPNX", "CPN",
        "CPEA0", "CPEB0", "CPI0", "CPEA1", "CPEB1", "CPI1",
        "CP00", "CP01", "CP10", "CP11",
    };
    private static readonly string[] NotNames = { "NOTA", "IW0", "IW1" };

    /// <summary>The persisted input signal names of the groups with named unconnected pins (issue #1025).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedInputSignalNames = new()
    {
        ["CPAX"] = new() { ["A"] = AddressSignal },
        ["NOTA"] = new() { ["A"] = AddressSignal },
        ["EN0"] = new() { ["A"] = LoadSignal },
        ["EN1"] = new() { ["A"] = LoadSignal },
        ["LE00"] = new() { ["A"] = LowDataSignal },
        ["LE01"] = new() { ["A"] = HighDataSignal },
        ["LE10"] = new() { ["A"] = LowDataSignal },
        ["LE11"] = new() { ["A"] = HighDataSignal },
    };

    /// <summary>The persisted output signal names per gate group — the read taps R and the stored-word taps W.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedOutputSignalNames = new()
    {
        ["OUT0"] = new() { ["Y"] = LowReadTap },
        ["OUT1"] = new() { ["Y"] = HighReadTap },
        ["CP00"] = new() { ["Y2"] = "W0D0" },
        ["CP01"] = new() { ["Y2"] = "W0D1" },
        ["CP10"] = new() { ["Y2"] = "W1D0" },
        ["CP11"] = new() { ["Y2"] = "W1D1" },
    };

    private readonly RamFixture _fixture;

    /// <summary>Attaches the shared RAM fixture.</summary>
    public LogicGateRam2x2ExampleTests(RamFixture fixture) => _fixture = fixture;

    /// <summary>The resting input bits: LOAD low (hold), the address and data inputs low.</summary>
    private static Dictionary<string, bool> HoldingInputBits() =>
        Bits(addr: false, load: false, d0: false, d1: false);

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_WithPersistedRolesAndRegisterDesignations()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the RAM contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(49,
            "49 wires join the 37 gates: the address/NA trees, the per-word enable "
            + "cascades, the register feedbacks, the read taps and the read MUX — every "
            + "waveguide keeps one driver and one load");

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
                roles.IsRegister.ShouldBeFalse($"the inverter '{group.GroupName}' is combinational");
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
    public void AssembledNetwork_ExposesAddrLoadDataTogglesTheNamedTapsAndFourRegisters()
    {
        _fixture.Network.InputPinNames.ShouldBe(
            new[] { AddressSignal, LoadSignal, LowDataSignal, HighDataSignal },
            ignoreOrder: true,
            customMessage: "the signal names merge the unconnected pins into exactly four "
                + "network inputs (issue #1025) — ADDR drives NOTA and CPAX, LOAD drives "
                + "EN0/EN1, D0/D1 drive their load arms — without a wire");
        _fixture.Network.OutputPinNames.ShouldContain(LowReadTap);
        _fixture.Network.OutputPinNames.ShouldContain(HighReadTap);
        _fixture.Network.RegisterState.Keys.ShouldBe(
            RegisterNames.Select(name => new LogicPinRef(name, "Y")).ToArray(),
            ignoreOrder: true,
            customMessage: "all four word bits power up as state elements, committed "
                + "outputs cleared");
    }

    [Fact]
    public async Task BusView_GroupsDataAndReadBitsIntoDecimalRows_StartingAtZero()
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
            .ShouldBe(new[] { AddressSignal, LoadSignal }, ignoreOrder: true,
                "ADDR and LOAD have no indexed family and stay plain toggles");

        var outputBuses = vm.OutputRows.OfType<LogicSignalBusOutputViewModel>().ToList();
        outputBuses.Select(b => b.Prefix).ShouldBe(new[] { "W0D", "W1D", "R" },
            "the two stored-word tap pairs group into their own decimal rows and R0/R1 "
            + "into the read bus row (issue #1068)");
        var busR = outputBuses.Single(b => b.Prefix == "R");
        busR.Members.Select(m => m.PinName).ShouldBe(new[] { "R0", "R1" });
        busR.DecimalValue.ShouldBe(0, "the RAM powers up cleared: the read bus R reads 0");
    }

    [Fact]
    public void StepSequence_StoreWord0ThenWord1_IsolatesTheAddressedWord()
    {
        AssertStoreIsolation(_fixture.Network);
    }

    [Fact]
    public void StepSequence_LoadLow_HoldsBothWordsAcrossSteps()
    {
        AssertHoldAcrossSteps(_fixture.Network);
    }

    /// <summary>
    /// Drives the RAM back into the powered-up-cleared state: LOAD=1 with D=0 commits
    /// 0 into the currently addressed word, and the same for the other address. Fact
    /// execution order is unspecified, so every sequence pins its own starting state.
    /// </summary>
    private static void ResetToZero(LogicNetworkEvaluator network)
    {
        foreach (var addr in new[] { false, true })
        {
            network.Evaluate(Bits(addr, load: true, d0: false, d1: false));
            network.Step();
        }
        ReadWord(network, addr: false).ShouldBe(0, "word 0 cleared");
        ReadWord(network, addr: true).ShouldBe(0, "word 1 cleared");
    }

    /// <summary>Reads the 2-bit word at the address: LOAD low, one evaluate, R as a decimal.</summary>
    private static int ReadWord(LogicNetworkEvaluator network, bool addr)
    {
        var read = network.Evaluate(Bits(addr, load: false, d0: false, d1: false));
        return (read[HighReadTap] ? 2 : 0) + (read[LowReadTap] ? 1 : 0);
    }

    /// <summary>Stores one 2-bit word at the address: ADDR + LOAD + D, one clock commit.</summary>
    private static void StoreWord(LogicNetworkEvaluator network, bool addr, int d)
    {
        network.Evaluate(Bits(addr, load: true, d0: (d & 1) != 0, d1: (d & 2) != 0));
        network.Step();
    }

    /// <summary>
    /// Asserts the pinned store sequence (acceptance criteria 2 + 3): store D=2 at
    /// ADDR=0, store D=1 at ADDR=1 — reading ADDR=1 then answers R=1 while ADDR=0
    /// still answers R=2: the addressed word alone commits.
    /// </summary>
    private static void AssertStoreIsolation(LogicNetworkEvaluator network)
    {
        ResetToZero(network);

        StoreWord(network, addr: false, d: 2);
        ReadWord(network, addr: false).ShouldBe(2,
            "ADDR=0, LOAD=1, D=2 + clock: reading ADDR=0 shows R=2");
        ReadWord(network, addr: true).ShouldBe(0,
            "word 1 is untouched: reading ADDR=1 still shows the cleared 0");

        StoreWord(network, addr: true, d: 1);
        ReadWord(network, addr: true).ShouldBe(1,
            "ADDR=1, LOAD=1, D=1 + clock: reading ADDR=1 shows R=1");
        ReadWord(network, addr: false).ShouldBe(2,
            "isolation: reading ADDR=0 still shows R=2 — the addressed word alone commits");
    }

    /// <summary>
    /// Asserts the pinned hold sequence (acceptance criterion 4): after the two stores,
    /// LOAD=0 holds both words across further clock steps while the data inputs rest.
    /// </summary>
    private static void AssertHoldAcrossSteps(LogicNetworkEvaluator network)
    {
        AssertStoreIsolation(network);

        network.Evaluate(HoldingInputBits());
        network.Step();
        network.Step();
        ReadWord(network, addr: false).ShouldBe(2,
            "LOAD=0: word 0 holds R=2 across two steps");
        ReadWord(network, addr: true).ShouldBe(1,
            "LOAD=0: word 1 holds R=1 across two steps");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameRam()
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
            reloadedCanvas.Connections.Count.ShouldBe(49,
                "every RAM wire — the register feedbacks and read taps included — must "
                + "survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");

            AssertStoreIsolation(reloaded);
            AssertHoldAcrossSteps(reloaded);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>The network input bits for one ADDR/LOAD/D0/D1 quadruple — one bit per signal (issue #1025).</summary>
    private static Dictionary<string, bool> Bits(bool addr, bool load, bool d0, bool d1) =>
        new()
        {
            [AddressSignal] = addr,
            [LoadSignal] = load,
            [LowDataSignal] = d0,
            [HighDataSignal] = d1,
        };

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class RamFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate RAM 2x2.lun";

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
            var path = Path.Combine(Path.GetTempPath(), $"ram-2x2-{Guid.NewGuid():N}.lun");
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

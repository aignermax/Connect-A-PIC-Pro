using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Full Adder.lun</c> (issue #990,
/// rung 4→5 of the NAND game): 32 top-level instances of the NOT/NAND gate — 28 read
/// as NAND, four as NOT — wired to the textbook full adder (two half adders + carry-OR).
/// The file loads through the real load path, every group carries its persisted
/// <see cref="TruthTablePinAssignment"/>, and the merged <see cref="LogicNetworkAssembler"/>
/// (#988) turns the loaded design into the evaluable network: Sum = A⊕B⊕Cin at
/// <c>H2SUM.Y</c>, Cout = majority(A, B, Cin) at <c>OROUT.Y</c>, evaluated by pure table
/// lookup. Like in the half adder (#987), shared stages are duplicated (the partial sum
/// S1 = A⊕B fans out onto half adder 2's four operand pins, so half adder 1's XOR ladder
/// is instantiated four times, H1SUM1–H1SUM4) because the canvas wires one waveguide per
/// pin — the fan-out of A, B and Cin happens at the logic layer, where the persisted
/// signal names (issue #1025) merge every operand's pins into one network input each:
/// exactly the three toggles A, B and Cin, although the addend pins and the carry-in
/// pins all share the bare pin name A on their gates. The outputs carry signal names,
/// too (#1046): the sum tap reads <c>S</c> (<c>H2SUM.Y</c>) and the carry-out tap
/// <c>Cout</c> (<c>OROUT.Y</c>). The carry-OR reads the two
/// half-adder carries through two NOTs and one NAND: Cout = NAND(¬C1, ¬C2) = C1∨C2.
/// </summary>
public class LogicGateFullAdderExampleTests : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;

    private static readonly string[] GateNames =
    {
        "H1N1A1", "H1N1B1", "H1N21", "H1N31", "H1SUM1",
        "H1N1A2", "H1N1B2", "H1N22", "H1N32", "H1SUM2",
        "H1N1A3", "H1N1B3", "H1N23", "H1N33", "H1SUM3",
        "H1N1A4", "H1N1B4", "H1N24", "H1N34", "H1SUM4",
        "H1N5", "H1CARRY",
        "H2N1A", "H2N1B", "H2N2", "H2N3", "H2SUM", "H2N5", "H2CARRY",
        "ORNOT1", "ORNOT2", "OROUT",
    };

    private static readonly string[] NotGateNames = { "H1CARRY", "H2CARRY", "ORNOT1", "ORNOT2" };

    /// <summary>The persisted input signal names per gate group (issue #1025); null = no named pins.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedSignalNames =
        BuildExpectedSignalNames();

    /// <summary>The persisted output signal names per gate group (issue #1046); null = no named outputs.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedOutputSignalNames =
        GateNames.ToDictionary(
            name => name,
            name => name == "H2SUM"
                ? new Dictionary<string, string> { ["Y"] = "S" }
                : name == "OROUT"
                    ? new Dictionary<string, string> { ["Y"] = "Cout" }
                    : null);

    private readonly FullAdderFixture _fixture;

    /// <summary>Attaches the shared full-adder fixture.</summary>
    public LogicGateFullAdderExampleTests(FullAdderFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the full adder contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(30, "thirty wires join the 32 gates");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            var isNot = NotGateNames.Contains(group.GroupName);
            roles.InputPinNames.ShouldBe(isNot ? new[] { "A" } : new[] { "A", "B" });
            roles.OutputPinNames.ShouldBe(new[] { "Y" });
            roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            roles.Threshold.ShouldBe(isNot ? NotThreshold : NandThreshold);
            roles.InputSignalNames.ShouldBe(ExpectedSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            roles.OutputSignalNames.ShouldBe(ExpectedOutputSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its output signal names (issue #1046)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesThreeOperandSignalsAndEveryGateOutputAsTap()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { "A", "B", "Cin" }, ignoreOrder: true,
            customMessage: "the signal names merge the 30 operand pins into exactly three network " +
                "inputs (issue #1025) — A and Cin stay separate although their pins share the bare pin name A");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Select(name => name == "H2SUM" ? "S" : name == "OROUT" ? "Cout" : $"{name}.Y").ToArray(),
            ignoreOrder: true,
            customMessage: "every gate output is a tap — the named outputs read S and Cout (issue #1046)");
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void LogicLayer_AllInputCombinations_YieldFullAdderSumAndCarryOut(
        bool a, bool b, bool cin, bool expectedSum, bool expectedCout)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b, cin));

        result["S"].ShouldBe(expectedSum, "Sum = A XOR B XOR Cin");
        result["Cout"].ShouldBe(expectedCout, "Cout = majority(A, B, Cin)");

        var sum1 = a ^ b;
        result["H1SUM1.Y"].ShouldBe(sum1, "half adder 1's sum stays readable as a tap");
        result["H1SUM2.Y"].ShouldBe(sum1, "the duplicated sum stage reads identically");
        result["H1SUM3.Y"].ShouldBe(sum1, "the duplicated sum stage reads identically");
        result["H1SUM4.Y"].ShouldBe(sum1, "the duplicated sum stage reads identically");
        result["H1CARRY.Y"].ShouldBe(a && b, "half adder 1's carry stays readable as a tap");
        result["H2CARRY.Y"].ShouldBe(sum1 && cin, "half adder 2's carry stays readable as a tap");
    }

    [Fact]
    public async Task LogicPanel_Outputs_ShowSignalNames_WithRawPinIdsRidingAlong()
    {
        // Issue #1046: the Logic panel reads S and Cout instead of the raw gate pin
        // ids; the tooltip text keeps the tapped gate.pin reachable.
        var vm = new LogicPanelViewModel();
        vm.Configure(_fixture.Canvas);

        await vm.BuildNetworkCommand.ExecuteAsync(null);

        vm.HasNetwork.ShouldBeTrue(vm.StatusText);
        var sum = vm.Outputs.Single(o => o.PinName == "S");
        sum.RawPinName.ShouldBe("H2SUM.Y", "the raw pin id rides along for the tooltip");
        var cout = vm.Outputs.Single(o => o.PinName == "Cout");
        cout.RawPinName.ShouldBe("OROUT.Y");
        vm.Outputs.ShouldNotContain(o => o.PinName == "H2SUM.Y" || o.PinName == "OROUT.Y",
            "named outputs no longer surface as raw gate pin ids");
        var unnamed = vm.Outputs.Single(o => o.PinName == "H1SUM1.Y");
        unnamed.RawPinName.ShouldBe("H1SUM1.Y", "an unnamed output keeps its raw id — display and tooltip coincide");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameFullAdder()
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
                group.TruthTablePinAssignment!.InputSignalNames.ShouldBe(
                    ExpectedSignalNames[group.GroupName],
                    $"the signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
                group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                    ExpectedOutputSignalNames[group.GroupName],
                    $"the output signal names of '{group.GroupName}' must survive the save → load round trip (#1046)");
            }
            reloadedCanvas.Connections.Count.ShouldBe(30,
                "every gate wire must survive the save → load round trip");

            var reloaded = await AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var a in new[] { false, true })
            foreach (var b in new[] { false, true })
            foreach (var cin in new[] { false, true })
            {
                var expected = _fixture.Network.Evaluate(_fixture.InputBits(a, b, cin));
                reloaded.Evaluate(_fixture.InputBits(a, b, cin)).ShouldBe(expected,
                    $"the re-assembled network must evaluate identically for A={a}, B={b}, Cin={cin}");
            }
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>
    /// The expected persisted signal names per gate group: half adder 1's operand
    /// pins carry the addends A and B, half adder 2's unconnected pins carry the
    /// carry-in Cin; fully wired pins (the sum stages, the carries, the OR) ship
    /// no names — a driven pin needs no network-signal identity.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>?> BuildExpectedSignalNames()
    {
        var addendAB = new Dictionary<string, string> { ["A"] = "A", ["B"] = "B" };
        var expected = new Dictionary<string, Dictionary<string, string>?>();
        for (var copy = 1; copy <= 4; copy++)
        {
            expected[$"H1N1A{copy}"] = addendAB;
            expected[$"H1N1B{copy}"] = addendAB;
            expected[$"H1N2{copy}"] = new() { ["A"] = "A" };
            expected[$"H1N3{copy}"] = new() { ["B"] = "B" };
            expected[$"H1SUM{copy}"] = null;
        }
        expected["H1N5"] = addendAB;
        expected["H1CARRY"] = null;
        foreach (var name in new[] { "H2N1A", "H2N1B", "H2N2", "H2N5" })
            expected[name] = new() { ["A"] = "Cin" };
        foreach (var name in new[] { "H2N3", "H2SUM", "H2CARRY", "ORNOT1", "ORNOT2", "OROUT" })
            expected[name] = null;
        return expected;
    }

    /// <summary>
    /// Assembles the logic network exactly as the canvas states it: the merged
    /// <see cref="LogicNetworkAssembler"/> re-extracts every gate's truth table at the
    /// roles persisted in the file and derives the wiring from the design's connections.
    /// </summary>
    internal static async Task<LogicNetworkEvaluator> AssembleNetwork(DesignCanvasViewModel canvas)
    {
        var components = canvas.Components.Select(c => c.Component).ToList();
        var connections = canvas.Connections.Select(c => c.Connection).ToList();
        return await new LogicNetworkAssembler().AssembleAsync(
            components, connections, FullAdderFixture.WavelengthNm);
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class FullAdderFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Full Adder.lun";

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
            Network = await AssembleNetwork(Canvas);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>The network input bits for one operand triple — one bit per signal (issue #1025).</summary>
        public Dictionary<string, bool> InputBits(bool a, bool b, bool cin) =>
            new() { ["A"] = a, ["B"] = b, ["Cin"] = cin };

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"full-adder-{Guid.NewGuid():N}.lun");
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

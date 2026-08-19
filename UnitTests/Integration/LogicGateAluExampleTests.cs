using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate ALU 1-bit.lun</c> (issue #1070,
/// rung 5 datapath stone of the NAND game): nine top-level instances of the NOT/NAND
/// gate composing a 1-bit ALU whose <c>Op</c> toggle steers the shipped 2-to-1 MUX
/// composition (#1059) between the two computed functions <c>AND(A, B)</c> and
/// <c>OR(A, B)</c> — the smallest circuit that teaches "the select line picks the
/// datapath". The AND datapath is NAND + NOT, the OR datapath is De Morgan
/// (NOT, NOT, NAND); both feed the MUX, and the final NAND's output carries the
/// persisted output signal name <c>Result</c> (#1046). The file loads through the
/// real load path, every group carries its persisted <see cref="TruthTablePinAssignment"/>,
/// and the merged <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design
/// into the evaluable network: Op = 0 ⇒ Result = A∧B, Op = 1 ⇒ Result = A∨B,
/// evaluated by pure table lookup. The fan-out of A, B and Op onto several gates
/// happens at the logic layer, where the persisted signal names (issue #1025) merge
/// the six operand pins into exactly the three toggles A, B and Op — no gate needs
/// duplication here because every gate output drives exactly one gate input.
/// </summary>
public class LogicGateAluExampleTests : IClassFixture<LogicGateAluExampleTests.AluFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;

    private static readonly string[] GateNames =
        { "AND1", "AND2", "ORN1", "ORN2", "OR1", "NOT1", "NANDA", "NANDB", "Out" };

    /// <summary>The gates read as NOT (single input, higher threshold); the rest read as NAND.</summary>
    private static readonly string[] NotGateNames = { "AND2", "ORN1", "ORN2", "NOT1" };

    /// <summary>The persisted input signal names per gate group (issue #1025); null = no named pins.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedInputSignalNames = new()
    {
        ["AND1"] = new() { ["A"] = "A", ["B"] = "B" },
        ["AND2"] = null,
        ["ORN1"] = new() { ["A"] = "A" },
        ["ORN2"] = new() { ["A"] = "B" },
        ["OR1"] = null,
        ["NOT1"] = new() { ["A"] = "Op" },
        ["NANDA"] = null,
        ["NANDB"] = new() { ["A"] = "Op" },
        ["Out"] = null,
    };

    private readonly AluFixture _fixture;

    /// <summary>Attaches the shared ALU fixture.</summary>
    public LogicGateAluExampleTests(AluFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the ALU contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(8, "eight wires join the nine gates");

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
            roles.InputSignalNames.ShouldBe(ExpectedInputSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            roles.OutputSignalNames.ShouldBe(
                group.GroupName == "Out" ? new() { ["Y"] = "Result" } : null,
                "only the final gate names its output Result (issue #1046)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesThreeTogglesAndTheNamedResultOutput()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { "A", "B", "Op" }, ignoreOrder: true,
            customMessage: "the signal names merge the six operand pins into exactly three network " +
                "inputs (issue #1025) — the select toggle Op drives NOT1.A and NANDB.A without a wire");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Where(n => n != "Out").Select(n => $"{n}.Y").Append("Result").ToArray(),
            ignoreOrder: true,
            customMessage: "every intermediate gate stays readable as a tap; the ALU output " +
                "reads Result instead of Out.Y (issue #1046)");
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public void LogicLayer_AllInputCombinations_YieldTheSelectedAluFunction(
        bool a, bool b, bool op, bool expectedResult)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b, op));

        result["Result"].ShouldBe(expectedResult,
            "Op = 0 selects AND(A, B), Op = 1 selects OR(A, B)");
        result["AND1.Y"].ShouldBe(!(a && b), "the AND datapath's NAND stage stays readable as a tap");
        result["AND2.Y"].ShouldBe(a && b, "the AND datapath result stays readable as a tap");
        result["ORN1.Y"].ShouldBe(!a, "the OR datapath's A inverter stays readable as a tap");
        result["ORN2.Y"].ShouldBe(!b, "the OR datapath's B inverter stays readable as a tap");
        result["OR1.Y"].ShouldBe(a || b, "the OR datapath result stays readable as a tap");
        result["NOT1.Y"].ShouldBe(!op, "the select inverter stays readable as a tap");
        result["NANDA.Y"].ShouldBe(!(a && b && !op), "the AND-side select term stays readable as a tap");
        result["NANDB.Y"].ShouldBe(!((a || b) && op), "the OR-side select term stays readable as a tap");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameAlu()
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
                    ExpectedInputSignalNames[group.GroupName],
                    $"the signal names of '{group.GroupName}' must survive the save → load round trip (#1025)");
                group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                    group.GroupName == "Out" ? new() { ["Y"] = "Result" } : null,
                    $"the output name of '{group.GroupName}' must survive the save → load round trip (#1046)");
            }
            reloadedCanvas.Connections.Count.ShouldBe(8,
                "every gate wire must survive the save → load round trip");

            var reloaded = await AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var a in new[] { false, true })
            foreach (var b in new[] { false, true })
            foreach (var op in new[] { false, true })
            {
                var expected = _fixture.Network.Evaluate(_fixture.InputBits(a, b, op));
                reloaded.Evaluate(_fixture.InputBits(a, b, op)).ShouldBe(expected,
                    $"the re-assembled network must evaluate identically for A={a}, B={b}, Op={op}");
            }
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
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
            components, connections, AluFixture.WavelengthNm);
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class AluFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate ALU 1-bit.lun";

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

        /// <summary>The network input bits for one operand/select triple — one bit per signal (issue #1025).</summary>
        public Dictionary<string, bool> InputBits(bool a, bool b, bool op) =>
            new() { ["A"] = a, ["B"] = b, ["Op"] = op };

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"alu-1bit-{Guid.NewGuid():N}.lun");
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

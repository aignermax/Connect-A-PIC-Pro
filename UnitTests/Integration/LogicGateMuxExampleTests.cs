using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate MUX.lun</c> (issue #1059,
/// rung 5 datapath stone of the NAND game): four top-level instances of the NOT/NAND
/// gate — three read as NAND, one as NOT — wired to the textbook 2-to-1 multiplexer
/// Out = NAND(NAND(A, NOT(Sel)), NAND(B, Sel)). The file loads through the real load
/// path, every group carries its persisted <see cref="TruthTablePinAssignment"/>, and
/// the merged <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into
/// the evaluable network: Sel = 0 ⇒ Out = A, Sel = 1 ⇒ Out = B at <c>Out.Y</c>,
/// evaluated by pure table lookup. The fan-out of Sel onto NOT1 and NANDB happens at
/// the logic layer, where the persisted signal names (issue #1025) merge the select
/// pins into the single network input <c>Sel</c> — exactly the three toggles A, B and
/// Sel, one named output <c>Out</c>. Unlike the adders, no shared stage needs
/// duplication here: the MUX's only fan-out (Sel) is between unconnected pins, which
/// the signal names merge without a waveguide.
/// </summary>
public class LogicGateMuxExampleTests : IClassFixture<LogicGateMuxExampleTests.MuxFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;

    private static readonly string[] GateNames = { "NOT1", "NANDA", "NANDB", "Out" };

    /// <summary>The persisted signal names per gate group (issue #1025); null = no named pins.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>?> ExpectedSignalNames = new()
    {
        ["NOT1"] = new() { ["A"] = "Sel" },
        ["NANDA"] = new() { ["A"] = "A" },
        ["NANDB"] = new() { ["A"] = "Sel", ["B"] = "B" },
        ["Out"] = null,
    };

    private readonly MuxFixture _fixture;

    /// <summary>Attaches the shared MUX fixture.</summary>
    public LogicGateMuxExampleTests(MuxFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the multiplexer contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(3, "three wires join the four gates");

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
            roles.InputSignalNames.ShouldBe(ExpectedSignalNames[group.GroupName],
                $"group '{group.GroupName}' ships its network-signal identity (issue #1025)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesThreeTogglesAndEveryGateOutputAsTap()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { "A", "B", "Sel" }, ignoreOrder: true,
            customMessage: "the signal names merge the five operand pins into exactly three network " +
                "inputs (issue #1025) — the select toggle Sel drives NOT1.A and NANDB.A without a wire");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Select(name => $"{name}.Y").ToArray(), ignoreOrder: true);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public void LogicLayer_AllInputCombinations_YieldTheMuxFunction(
        bool a, bool b, bool sel, bool expectedOut)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b, sel));

        result["Out.Y"].ShouldBe(expectedOut, "Out = A when Sel = 0, B when Sel = 1");
        result["NOT1.Y"].ShouldBe(!sel, "the select inverter stays readable as a tap");
        result["NANDA.Y"].ShouldBe(!(a && !sel), "the A-side select term stays readable as a tap");
        result["NANDB.Y"].ShouldBe(!(b && sel), "the B-side select term stays readable as a tap");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameMux()
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
            }
            reloadedCanvas.Connections.Count.ShouldBe(3,
                "every gate wire must survive the save → load round trip");

            var reloaded = await AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var a in new[] { false, true })
            foreach (var b in new[] { false, true })
            foreach (var sel in new[] { false, true })
            {
                var expected = _fixture.Network.Evaluate(_fixture.InputBits(a, b, sel));
                reloaded.Evaluate(_fixture.InputBits(a, b, sel)).ShouldBe(expected,
                    $"the re-assembled network must evaluate identically for A={a}, B={b}, Sel={sel}");
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
            components, connections, MuxFixture.WavelengthNm);
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class MuxFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate MUX.lun";

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
        public Dictionary<string, bool> InputBits(bool a, bool b, bool sel) =>
            new() { ["A"] = a, ["B"] = b, ["Sel"] = sel };

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"mux-{Guid.NewGuid():N}.lun");
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

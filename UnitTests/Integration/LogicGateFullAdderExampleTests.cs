using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Full Adder.lun</c> (issue #990,
/// NAND game rung 4→5): 26 top-level instances of the NOT/NAND gate — 25 read as NAND,
/// one (MHN) as NOT — wired to a full adder. The file loads through the real load path,
/// every group carries its persisted <see cref="TruthTablePinAssignment"/>, and the
/// <see cref="LogicNetworkAssembler"/> derives the evaluable network from the canvas
/// wiring: Sum = A⊕B⊕Cin at <c>SUM.Y</c>, Carry-Out = Majority(A,B,Cin) at
/// <c>COUT.Y</c>, evaluated by pure table lookup. The textbook second half-adder's
/// shared NAND(S1,Cin) stage is instantiated twice (NA/NB) and half-adder 1's Sum is
/// copied three times (S1/S2/S3) because the canvas wires one waveguide per pin — the
/// fan-out of A, B and Cin happens at the logic layer. The carry majority is computed
/// directly as NAND3 of the pairwise NANDs instead of OR-of-ANDs (that formulation
/// would need a fourth Sum copy); it still reads as half-adders + carry cluster.
/// </summary>
public class LogicGateFullAdderExampleTests : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;
    private const string NotGateName = "MHN";

    private static readonly string[] GateNames =
    {
        "DA1", "DB1", "P1", "Q1", "S1",
        "DA2", "DB2", "P2", "Q2", "S2",
        "DA3", "DB3", "P3", "Q3", "S3",
        "NA", "NB", "SP", "SQ", "SUM",
        "MAB", "MAC", "MBC", "MH", "MHN", "COUT",
    };

    /// <summary>Network inputs driven by addend A (fan-out at the logic layer).</summary>
    private static readonly string[] InputsA =
        { "DA1.A", "DB1.A", "P1.A", "DA2.A", "DB2.A", "P2.A", "DA3.A", "DB3.A", "P3.A", "MAB.A", "MAC.A" };

    /// <summary>Network inputs driven by addend B (fan-out at the logic layer).</summary>
    private static readonly string[] InputsB =
        { "DA1.B", "DB1.B", "Q1.B", "DA2.B", "DB2.B", "Q2.B", "DA3.B", "DB3.B", "Q3.B", "MAB.B", "MBC.A" };

    /// <summary>Network inputs driven by the carry-in (fan-out at the logic layer).</summary>
    private static readonly string[] InputsCin = { "NA.B", "NB.B", "SQ.B", "MAC.B", "MBC.B" };

    private const int ExpectedConnectionCount = 24;

    private readonly FullAdderFixture _fixture;

    /// <summary>Attaches the shared full-adder fixture.</summary>
    public LogicGateFullAdderExampleTests(FullAdderFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the full adder contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(
            ExpectedConnectionCount, "24 one-wire-per-pin links join the 26 gates");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            var isNot = group.GroupName == NotGateName;
            roles.InputPinNames.ShouldBe(isNot ? new[] { "A" } : new[] { "A", "B" });
            roles.OutputPinNames.ShouldBe(new[] { "Y" });
            roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            roles.Threshold.ShouldBe(isNot ? NotThreshold : NandThreshold);
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesThreeInputsAndEveryGateOutputAsTap()
    {
        _fixture.Network.InputPinNames.ShouldBe(
            InputsA.Concat(InputsB).Concat(InputsCin).ToArray(), ignoreOrder: true,
            customMessage: "A, B and Cin fan out to the gate inputs at the logic layer");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Select(name => $"{name}.Y").ToArray(), ignoreOrder: true);
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void LogicLayer_AllEightInputCombinations_YieldFullAdderSumAndCarry(
        bool a, bool b, bool cin, bool expectedSum, bool expectedCarry)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b, cin));

        result["SUM.Y"].ShouldBe(expectedSum, "Sum = A XOR B XOR Cin");
        result["COUT.Y"].ShouldBe(expectedCarry, "Carry-Out = Majority(A, B, Cin)");
        result["S1.Y"].ShouldBe(a ^ b, "Sum copy 1 stays readable as a tap");
        result["S2.Y"].ShouldBe(a ^ b, "the duplicated Sum copy reads identically");
        result["MAB.Y"].ShouldBe(!(a && b), "the majority's pairwise NAND stays readable as a tap");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameFullAdder()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LoadCanvas(savedPath);

            var reloadedGroups = GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            reloadedGroups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
                "the persisted pin roles must survive the save → load round trip");
            reloadedCanvas.Connections.Count.ShouldBe(
                ExpectedConnectionCount, "every gate wire must survive the round trip");

            var reloaded = await AssembleNetwork(reloadedCanvas, reloadedGroups);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var (a, b, cin) in AllCombinations())
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

    /// <summary>All eight input-bit combinations of the 3-input adder.</summary>
    private static IEnumerable<(bool A, bool B, bool Cin)> AllCombinations()
    {
        foreach (var a in new[] { false, true })
        foreach (var b in new[] { false, true })
        foreach (var cin in new[] { false, true })
            yield return (a, b, cin);
    }

    /// <summary>Loads a design file through the real load path onto a fresh canvas.</summary>
    internal static async Task<DesignCanvasViewModel> LoadCanvas(string path)
    {
        var canvas = new DesignCanvasViewModel();
        var fileOps = CreateFileOperations(canvas);
        (await fileOps.LoadDesignFromPathAsync(path)).ShouldBeTrue(
            $"'{Path.GetFileName(path)}' must load through the real load path");
        return canvas;
    }

    /// <summary>The canvas's top-level gate groups, in file order.</summary>
    internal static List<ComponentGroup> GroupsOf(DesignCanvasViewModel canvas) =>
        canvas.Components.Select(c => c.Component).OfType<ComponentGroup>().ToList();

    /// <summary>
    /// Assembles the logic network with the <see cref="LogicNetworkAssembler"/> exactly
    /// as the canvas states it: load-path wire endpoints (bound to the internal pins
    /// behind the external pins) are re-expressed onto the groups' synced external
    /// pins, then the assembler re-extracts every gate with its persisted roles and
    /// derives the wiring; nothing about the network is hand-built.
    /// </summary>
    internal static async Task<LogicNetworkEvaluator> AssembleNetwork(
        DesignCanvasViewModel canvas, IReadOnlyList<ComponentGroup> groups)
    {
        var components = canvas.Components.Select(c => c.Component).ToList();
        foreach (var group in groups)
        {
            group.EnsureSMatrixComputed();
        }
        var wires = canvas.Connections
            .Select(c => MapToGatePins(c.Connection, groups))
            .ToList();
        return await new LogicNetworkAssembler().AssembleAsync(
            components, wires, FullAdderFixture.WavelengthNm);
    }

    /// <summary>
    /// Re-expresses one canvas wire in terms of the gate groups' external pins: the
    /// load path binds wire endpoints to the internal component pins behind the
    /// external pins, while the assembler resolves wires against the group's own pins
    /// (synced by the extraction).
    /// </summary>
    private static WaveguideConnection MapToGatePins(
        WaveguideConnection wire, IReadOnlyList<ComponentGroup> groups) =>
        new() { StartPin = GatePin(wire.StartPin, groups), EndPin = GatePin(wire.EndPin, groups) };

    /// <summary>Maps a wire endpoint onto its gate group's synced external pin.</summary>
    private static PhysicalPin GatePin(PhysicalPin pin, IReadOnlyList<ComponentGroup> groups)
    {
        foreach (var group in groups)
        {
            var external = group.ExternalPins.FirstOrDefault(p => ReferenceEquals(p.InternalPin, pin));
            if (external != null)
                return group.PhysicalPins.Single(p => p.Name == external.Name);
        }
        throw new InvalidOperationException($"Wire endpoint '{pin.Name}' belongs to no gate group.");
    }

    internal static FileOperationsViewModel CreateFileOperations(DesignCanvasViewModel canvas)
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!);
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each re-extraction is a real simulation run), so every fact asserts against
    /// the same loaded design and assembled network.
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

        /// <summary>The logic network assembled from the loaded canvas wiring.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Loads the shipped example and assembles its logic network.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LoadCanvas(path);
            Groups = GroupsOf(Canvas);
            Network = await AssembleNetwork(Canvas, Groups);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>The network input bits for one addend triple (A, B, Cin fan out at the logic layer).</summary>
        public Dictionary<string, bool> InputBits(bool a, bool b, bool cin)
        {
            var bits = new Dictionary<string, bool>();
            foreach (var name in InputsA) bits[name] = a;
            foreach (var name in InputsB) bits[name] = b;
            foreach (var name in InputsCin) bits[name] = cin;
            return bits;
        }

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"full-adder-{Guid.NewGuid():N}.lun");
            var saveVm = CreateFileOperations(Canvas);
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

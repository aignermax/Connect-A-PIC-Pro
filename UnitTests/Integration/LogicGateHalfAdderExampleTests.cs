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
/// Pinned tests for the shipped <c>examples/Logic Gate Half Adder.lun</c> (issue #986,
/// rung 4→5 of the NAND game): seven top-level instances of the NOT/NAND gate — six
/// read as NAND, one as NOT — wired to a half adder. The file loads through the real
/// load path, every group carries its persisted <see cref="TruthTablePinAssignment"/>,
/// and <see cref="LogicNetworkBuilder"/> derives the network from the canvas wiring:
/// Sum = A⊕B at <c>NAND4.Y</c>, Carry = A∧B at <c>NOT1.Y</c>, evaluated by pure table
/// lookup. The textbook XOR's shared NAND(A,B) stage is instantiated twice
/// (NAND1A/NAND1B) because the canvas wires one waveguide per pin — the fan-out of A
/// and B happens at the logic layer, where each gate input is driven by the same
/// network bit. Composition at the logic layer restores ideal levels at every stage,
/// so — unlike the optical two-stage cascade — no S-matrix honesty bound applies here.
/// </summary>
public class LogicGateHalfAdderExampleTests : IClassFixture<LogicGateHalfAdderExampleTests.HalfAdderFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;

    private static readonly string[] GateNames =
        { "NAND1A", "NAND1B", "NAND2", "NAND3", "NAND4", "NAND5", "NOT1" };

    /// <summary>Network inputs driven by addend A (fan-out at the logic layer).</summary>
    private static readonly string[] InputsA = { "NAND1A.A", "NAND1B.A", "NAND2.A", "NAND5.A" };

    /// <summary>Network inputs driven by addend B (fan-out at the logic layer).</summary>
    private static readonly string[] InputsB = { "NAND1A.B", "NAND1B.B", "NAND3.B", "NAND5.B" };

    private readonly HalfAdderFixture _fixture;

    /// <summary>Attaches the shared half-adder fixture.</summary>
    public LogicGateHalfAdderExampleTests(HalfAdderFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the half adder contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(5, "five wires join the seven gates");

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
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void DerivedNetwork_ExposesAddendInputsAndEveryGateOutputAsTap()
    {
        _fixture.Network.InputPinNames.ShouldBe(
            InputsA.Concat(InputsB).ToArray(), ignoreOrder: true,
            customMessage: "the addends A and B fan out to four gate inputs each");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Select(name => $"{name}.Y").ToArray(), ignoreOrder: true);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    public void LogicLayer_AllInputCombinations_YieldHalfAdderSumAndCarry(
        bool a, bool b, bool expectedSum, bool expectedCarry)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b));

        result["NAND4.Y"].ShouldBe(expectedSum, "Sum = A XOR B");
        result["NOT1.Y"].ShouldBe(expectedCarry, "Carry = A AND B");
        result["NAND1A.Y"].ShouldBe(!(a && b), "the first-stage NAND stays readable as a tap");
        result["NAND1B.Y"].ShouldBe(!(a && b), "the duplicated first stage reads identically");
        result["NAND5.Y"].ShouldBe(!(a && b), "the carry NAND stays readable as a tap");
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReDerivedNetwork_YieldsTheSameHalfAdder()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LoadCanvas(savedPath);

            var reloadedGroups = GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            reloadedGroups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
                "the persisted pin roles must survive the save → load round trip");
            reloadedCanvas.Connections.Count.ShouldBe(5,
                "every gate wire must survive the save → load round trip");

            var reloaded = await BuildNetwork(reloadedCanvas, reloadedGroups);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var (a, b) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var expected = _fixture.Network.Evaluate(_fixture.InputBits(a, b));
                reloaded.Evaluate(_fixture.InputBits(a, b)).ShouldBe(expected,
                    $"the re-derived network must evaluate identically for A={a}, B={b}");
            }
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
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
    /// Derives the logic network exactly as the canvas states it: each gate's truth table
    /// is extracted through the real simulation at the roles persisted in the file, and
    /// the canvas wires become logic wires between the groups' external pins.
    /// </summary>
    internal static async Task<LogicNetworkEvaluator> BuildNetwork(
        DesignCanvasViewModel canvas, IReadOnlyList<ComponentGroup> groups)
    {
        var gates = new List<LogicGateInstance>(groups.Count);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment!;
            var table = await new TruthTableExtractor().ExtractAsync(
                group, roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames,
                roles.Threshold, HalfAdderFixture.WavelengthNm);
            gates.Add(new LogicGateInstance(
                group,
                LogicGateModel.FromTruthTable(table),
                new GateRoleAssignment(
                    roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames, roles.Threshold)));
        }

        var wires = canvas.Connections
            .Select(c => MapToGatePins(c.Connection, groups))
            .ToList();
        return new LogicNetworkBuilder().Build(gates, wires);
    }

    /// <summary>
    /// Re-expresses one canvas wire in terms of the gate groups' external pins: the
    /// load path binds wire endpoints to the internal component pins behind the
    /// external pins, while the builder resolves wires against the group's own pins
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
    /// Shared fixture: loads the shipped example once and derives its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and derived network.
    /// </summary>
    public class HalfAdderFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Half Adder.lun";

        /// <summary>Laser wavelength the persisted roles were extracted at.</summary>
        public const int WavelengthNm = 1550;

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network derived from the loaded canvas wiring.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Loads the shipped example and derives its logic network.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LoadCanvas(path);
            Groups = GroupsOf(Canvas);
            Network = await BuildNetwork(Canvas, Groups);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>The network input bits for one addend pair (A and B fan out at the logic layer).</summary>
        public Dictionary<string, bool> InputBits(bool a, bool b)
        {
            var bits = new Dictionary<string, bool>();
            foreach (var name in InputsA) bits[name] = a;
            foreach (var name in InputsB) bits[name] = b;
            return bits;
        }

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"half-adder-{Guid.NewGuid():N}.lun");
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

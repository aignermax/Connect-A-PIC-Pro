using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Export;
using UnitTests.Export.CornerstoneDrc;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Hard end-to-end scenario of the rung 4→7 chain (issue #995): the shipped
/// <c>examples/Logic Gate Full Adder.lun</c> — 32 gate groups wired to the textbook
/// full adder — must survive the whole manufacturing path, not just simulate. The
/// journey loads the design through the real load path, assembles the logic network
/// (<see cref="LogicNetworkAssembler"/>) and pins the corner combinations as the proof
/// it is *the* full adder, exports the design to GDS through the real nazca path,
/// and runs the vendored CORNERSTONE SiN pre-DRC deck headless over the export
/// (nazca/KLayout gated, same skip behavior as the GDS round-trips). The foundry-deck
/// verdict is pinned to an empty violation set: today the export writes only layers
/// none of the deck's rules inspect — any new violation class fails this journey.
/// </summary>
public class FullAdderManufacturingJourneyTests
    : IClassFixture<FullAdderManufacturingJourneyTests.ManufacturingJourneyFixture>
{
    private const string SumTap = "H2SUM.Y";
    private const string CarryOutTap = "OROUT.Y";

    private readonly ManufacturingJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public FullAdderManufacturingJourneyTests(ManufacturingJourneyFixture journey) => _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAsWiredGateGroupsWithPersistedRoles()
    {
        _journey.Groups.Select(g => g.GroupName)
            .ShouldBe(ExpectedGateNames, ignoreOrder: true);
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
            "every gate group must carry its persisted pin roles for the manufacturing path to matter");
        _journey.Canvas.Connections.Count.ShouldBe(30, "thirty wires join the thirty-two gates");
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, false, true, false, true)]
    public void Step2_AssembledNetwork_CornerCombinations_YieldFullAdderSumAndCarryOut(
        bool a, bool b, bool cin, bool expectedSum, bool expectedCarryOut)
    {
        var result = _journey.Network.Evaluate(_journey.InputBits(a, b, cin));

        result[SumTap].ShouldBe(expectedSum, "Sum = A XOR B XOR Cin");
        result[CarryOutTap].ShouldBe(expectedCarryOut, "Cout = majority(A, B, Cin)");
    }

    [Fact]
    public void Step3_NazcaExport_WritesTheDesignAsGdsTopCell()
    {
        _journey.NazcaScript.ShouldNotBeNullOrEmpty(
            "the real export path must produce a nazca script for the loaded full adder");
        _journey.NazcaScript.ShouldContain("nd.export_gds(topcells=[design]",
            Case.Sensitive, "the exported GDS carries the design as its top cell");
    }

    [Trait("Category", "Slow")]
    [SkippableFact]
    public async Task Step4_GdsExportAndFoundryDeck_EmptyViolationBaselineHolds()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the GDS export needs the real engine.");
        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null, "No KLayout on PATH/$KLAYOUT — the foundry-deck proof needs the real engine.");

        var exportDir = Path.Combine(_journey.WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "full_adder_manufacturing.py");
        await File.WriteAllTextAsync(scriptPath, _journey.NazcaScript);
        var export = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        export.ExitCode.ShouldBe(0, $"the nazca export of the full adder must succeed:\n{export.StdOut}\n{export.StdErr}");

        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"the export script must write {gdsPath}:\n{export.StdOut}");

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design");
        var designCell = library.Cells["ConnectAPIC_Design"];
        designCell.Elements.OfType<GdsReference>().ShouldNotBeEmpty(
            "the gate group children are placed as cell references");
        designCell.Elements.OfType<GdsPolygon>().ShouldNotBeEmpty(
            "the gate wiring flattens into real top-cell geometry");

        var reportPath = Path.Combine(exportDir, "full_adder.lyrdb");
        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", reportPath);

        exitCode.ShouldBe(0,
            $"the vendored foundry deck must complete.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.",
            Case.Sensitive,
            "the empty violation set is the pinned baseline today (the export targets none " +
            "of the deck's layers); a new violation class must fail this journey here");
    }

    private static readonly string[] ExpectedGateNames =
    {
        "H1N1A1", "H1N1B1", "H1N21", "H1N31", "H1SUM1",
        "H1N1A2", "H1N1B2", "H1N22", "H1N32", "H1SUM2",
        "H1N1A3", "H1N1B3", "H1N23", "H1N33", "H1SUM3",
        "H1N1A4", "H1N1B4", "H1N24", "H1N34", "H1SUM4",
        "H1N5", "H1CARRY",
        "H2N1A", "H2N1B", "H2N2", "H2N3", "H2SUM", "H2N5", "H2CARRY",
        "ORNOT1", "ORNOT2", "OROUT",
    };

    /// <summary>
    /// Shared journey fixture: performs the journey's stateful steps once (load →
    /// assemble → export-script) so each fact asserts one step of the same continuous
    /// journey and the DRC step drives exactly what the export produced.
    /// </summary>
    public class ManufacturingJourneyFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Full Adder.lun";

        /// <summary>Laser wavelength the persisted roles were extracted at.</summary>
        public const int WavelengthNm = 1550;

        /// <summary>Temp working directory for the GDS export and the DRC report.</summary>
        public string WorkDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "full-adder-manufacturing-" + Guid.NewGuid().ToString("N"));

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>The nazca export script of the loaded design.</summary>
        public string NazcaScript { get; private set; } = null!;

        /// <summary>Loads the shipped example, assembles its logic network, generates its export script.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
            Network = await LogicGateFullAdderExampleTests.AssembleNetwork(Canvas);
            NazcaScript = new SimpleNazcaExporter().Export(Canvas);
        }

        /// <summary>Removes the temp working directory.</summary>
        public Task DisposeAsync()
        {
            try
            {
                if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, recursive: true);
            }
            catch
            {
                // temp cleanup is best effort
            }
            return Task.CompletedTask;
        }

        /// <summary>The network input bits for one operand triple — one bit per signal (issue #1025).</summary>
        public Dictionary<string, bool> InputBits(bool a, bool b, bool cin) =>
            new() { ["A"] = a, ["B"] = b, ["Cin"] = cin };
    }
}

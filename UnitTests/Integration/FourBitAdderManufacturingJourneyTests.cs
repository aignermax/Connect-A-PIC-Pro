using System.Diagnostics;
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
/// Scale proof of the rung 4→7 manufacturing path (issue #1036), mirroring
/// <see cref="FullAdderManufacturingJourneyTests"/>: the shipped
/// <c>examples/Logic Gate 4-Bit Adder.lun</c> — 344 gate groups, an order of magnitude
/// above the 32-gate full adder (#995/#998) — must survive the same manufacturing path.
/// The journey loads the design through the real load path, pins one arithmetic
/// combination (9 + 7 + 1 = 17) against the assembled logic network, exports through
/// the real nazca path, and runs the vendored CORNERSTONE SiN pre-DRC deck headless
/// over the export (nazca/KLayout gated exactly like the full-adder journey). The deck
/// verdict is pinned to an empty violation set by the same layer argument: the nazca
/// export writes only layers none of the deck's rules inspect, and scale changes the
/// violation count, not the layer set — a new violation class must fail this journey.
/// Export wall clock and GDS size are recorded in the test output as scale evidence.
/// </summary>
public class FourBitAdderManufacturingJourneyTests
    : IClassFixture<FourBitAdderManufacturingJourneyTests.ManufacturingJourneyFixture>
{
    private const int GateCount = 344;
    private const int WireCount = 339;

    private readonly ManufacturingJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public FourBitAdderManufacturingJourneyTests(ManufacturingJourneyFixture journey) => _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAsWiredGateGroupsWithPersistedRoles()
    {
        _journey.Groups.Count.ShouldBe(GateCount, "four full-adder stages of gate groups");
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
            "every gate group must carry its persisted pin roles for the manufacturing path to matter");
        _journey.Canvas.Connections.Count.ShouldBe(WireCount, "339 wires join the 344 gates");
    }

    [Fact]
    public void Step2_AssembledNetwork_PinnedCombination_YieldsTheArithmeticSum()
    {
        // 9 + 7 + 1 = 17 = 0b1_0001: S0 set, S1–S3 clear, Cout set.
        const int a = 9;
        const int b = 7;
        const bool cin = true;
        var sum = a + b + (cin ? 1 : 0);

        var result = _journey.Network.Evaluate(
            LogicGateFourBitAdderExampleTests.FourBitAdderFixture.InputBits(a, b, cin));

        for (var stage = 0; stage < 4; stage++)
            result[$"T{stage}H2SUM.Y"].ShouldBe(((sum >> stage) & 1) == 1,
                $"S{stage} of {a} + {b} + 1 = {sum}");
        result["T3OROUT.Y"].ShouldBe(sum >= 16, "Cout of the 5-bit sum");
    }

    [Fact]
    public void Step3_NazcaExport_WritesTheDesignAsGdsTopCell()
    {
        _journey.NazcaScript.ShouldNotBeNullOrEmpty(
            "the real export path must produce a nazca script for the loaded 4-bit adder");
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
        var scriptPath = Path.Combine(exportDir, "four_bit_adder_manufacturing.py");
        await File.WriteAllTextAsync(scriptPath, _journey.NazcaScript);
        var exportWatch = Stopwatch.StartNew();
        var export = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        exportWatch.Stop();
        export.ExitCode.ShouldBe(0, $"the nazca export of the 4-bit adder must succeed:\n{export.StdOut}\n{export.StdErr}");

        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"the export script must write {gdsPath}:\n{export.StdOut}");
        Console.WriteLine($"[scale] 4-bit adder manufacturing: nazca export {exportWatch.Elapsed.TotalSeconds:F1} s, "
            + $"GDS {new FileInfo(gdsPath).Length:N0} bytes ({GateCount} gates / {WireCount} wires)");

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design");
        var designCell = library.Cells["ConnectAPIC_Design"];
        designCell.Elements.OfType<GdsReference>().ShouldNotBeEmpty(
            "the gate group children are placed as cell references");
        designCell.Elements.OfType<GdsPolygon>().ShouldNotBeEmpty(
            "the gate wiring flattens into real top-cell geometry");

        var reportPath = Path.Combine(exportDir, "four_bit_adder.lyrdb");
        var drcWatch = Stopwatch.StartNew();
        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", reportPath);
        drcWatch.Stop();
        Console.WriteLine($"[scale] 4-bit adder manufacturing: foundry deck {drcWatch.Elapsed.TotalSeconds:F1} s");

        exitCode.ShouldBe(0,
            $"the vendored foundry deck must complete.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.",
            Case.Sensitive,
            "the empty violation set is the pinned baseline (the export targets none " +
            "of the deck's layers at any gate count); a new violation class must fail this journey here");
    }

    /// <summary>
    /// Shared journey fixture: performs the journey's stateful steps once (load →
    /// assemble → export-script) so each fact asserts one step of the same continuous
    /// journey and the DRC step drives exactly what the export produced.
    /// </summary>
    public class ManufacturingJourneyFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate 4-Bit Adder.lun";

        /// <summary>Temp working directory for the GDS export and the DRC report.</summary>
        public string WorkDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "four-bit-adder-manufacturing-" + Guid.NewGuid().ToString("N"));

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
            Network = await LogicGateFourBitAdderExampleTests.AssembleNetwork(Canvas);
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
    }
}

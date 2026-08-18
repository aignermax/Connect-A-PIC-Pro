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
/// The rung 4→7 chain at the 4-bit adder's scale (issue #1036): the shipped
/// <c>examples/Logic Gate 4-Bit Adder.lun</c> — 344 gate groups joined by 339 wires,
/// an order of magnitude beyond the full adder's manufacturing journey (#995) — must
/// survive the whole manufacturing path, not just simulate. The journey loads the
/// design through the real load path, pins one arithmetic combination at the logic
/// layer as the proof it is *the* 4-bit adder, exports the design to GDS through the
/// real nazca path, and runs the vendored CORNERSTONE SiN pre-DRC deck headless over
/// the export (nazca/KLayout gated, same skip behavior as the GDS round-trips: skips
/// locally, runs in CI since #1026). The foundry-deck verdict is pinned to an empty
/// violation set: the export writes only layers none of the deck's geometry rules
/// inspect, exactly as the full adder's journey established — any new violation class
/// fails this journey. Export wall clock and GDS size are recorded in the test output
/// as the scale evidence for the PR (no hard threshold; a ~120 s export would be a
/// follow-up finding, not a failing assertion).
/// </summary>
public class FourBitAdderManufacturingJourneyTests
    : IClassFixture<FourBitAdderManufacturingJourneyTests.ManufacturingJourneyFixture>
{
    private const int ExpectedGateCount = 344;
    private const int ExpectedWireCount = 339;

    private readonly ManufacturingJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public FourBitAdderManufacturingJourneyTests(ManufacturingJourneyFixture journey) => _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAsWiredGateGroupsWithPersistedRoles()
    {
        _journey.Groups.Count.ShouldBe(ExpectedGateCount,
            "four stages × (32-gate base + duplicated carry copies)");
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
            "every gate group must carry its persisted pin roles for the manufacturing path to matter");
        _journey.Canvas.Connections.Count.ShouldBe(ExpectedWireCount, "339 wires join the 344 gates");
    }

    [Fact]
    public void Step2_AssembledNetwork_PinnedCombination_YieldsTheArithmeticSum()
    {
        // 9 + 7 + Cin=1 = 17 = 0b1_0001: the four sum bits read 0001, the ripple
        // leaves the 4-bit range as Cout — the one combination that pins every output.
        var result = _journey.Network.Evaluate(_journey.Adder.InputBits(9, 7, true));

        result["T0H2SUM.Y"].ShouldBeTrue("S0 of 9 + 7 + 1 = 17");
        result["T1H2SUM.Y"].ShouldBeFalse("S1 of 9 + 7 + 1 = 17");
        result["T2H2SUM.Y"].ShouldBeFalse("S2 of 9 + 7 + 1 = 17");
        result["T3H2SUM.Y"].ShouldBeFalse("S3 of 9 + 7 + 1 = 17");
        result["T3OROUT.Y"].ShouldBeTrue("Cout carries the 5th bit of 17");
    }

    [Fact]
    public void Step3_NazcaExport_WritesTheDesignAsGdsTopCell()
    {
        _journey.NazcaScript.ShouldNotBeNullOrEmpty(
            "the real export path must produce a nazca script for the loaded 4-bit adder");
        _journey.NazcaScript.ShouldContain("nd.export_gds(topcells=[design]",
            Case.Sensitive, "the exported GDS carries the design as its top cell");
        Console.WriteLine($"[scale] 4-bit adder nazca script ({ExpectedGateCount} gates): "
            + $"{_journey.NazcaScript.Length / 1024.0:F0} KiB, "
            + $"generated in {_journey.ScriptGenerationElapsed.TotalMilliseconds:F0} ms");
    }

    [Trait("Category", "Slow")]
    [SkippableFact]
    public async Task Step4_GdsExportAndFoundryDeck_EmptyViolationBaselineHoldsAtScale()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the GDS export needs the real engine.");

        var exportDir = Path.Combine(_journey.WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "four_bit_adder_manufacturing.py");
        await File.WriteAllTextAsync(scriptPath, _journey.NazcaScript);

        var watch = Stopwatch.StartNew();
        var export = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        watch.Stop();
        export.ExitCode.ShouldBe(0, $"the nazca export of the 4-bit adder must succeed:\n{export.StdOut}\n{export.StdErr}");

        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"the export script must write {gdsPath}:\n{export.StdOut}");
        Console.WriteLine($"[scale] 4-bit adder GDS export ({ExpectedGateCount} gates): "
            + $"{watch.Elapsed.TotalSeconds:F1} s wall clock, GDS {new FileInfo(gdsPath).Length / 1024.0:F0} KiB");

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design");
        var designCell = library.Cells["ConnectAPIC_Design"];
        designCell.Elements.OfType<GdsReference>().ShouldNotBeEmpty(
            "the gate group children are placed as cell references");
        designCell.Elements.OfType<GdsPolygon>().ShouldNotBeEmpty(
            "the gate wiring flattens into real top-cell geometry");

        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null, "No KLayout on PATH/$KLAYOUT — the foundry-deck proof needs the real engine.");

        var reportPath = Path.Combine(exportDir, "four_bit_adder.lyrdb");
        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", reportPath);

        exitCode.ShouldBe(0,
            $"the vendored foundry deck must complete.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.",
            Case.Sensitive,
            "the empty violation set is the pinned baseline at this scale too (the export targets none " +
            "of the deck's layers, same as the full adder's journey); a new violation class must fail " +
            "this journey here");
    }

    /// <summary>
    /// Shared journey fixture: performs the journey's stateful steps once (load →
    /// assemble → export-script) so each fact asserts one step of the same continuous
    /// journey and the DRC step drives exactly what the export produced. Load, network
    /// assembly and the logic-layer input mapping are delegated to the pinned 4-bit
    /// adder fixture (#1023) so this journey reuses — never forks — its gate model.
    /// </summary>
    public class ManufacturingJourneyFixture : IAsyncLifetime
    {
        /// <summary>The pinned 4-bit adder fixture carrying canvas, groups, network and input mapping.</summary>
        public LogicGateFourBitAdderExampleTests.FourBitAdderFixture Adder { get; } = new();

        /// <summary>Temp working directory for the GDS export and the DRC report.</summary>
        public string WorkDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "four-bit-adder-manufacturing-" + Guid.NewGuid().ToString("N"));

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas => Adder.Canvas;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups => Adder.Groups;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network => Adder.Network;

        /// <summary>The nazca export script of the loaded design.</summary>
        public string NazcaScript { get; private set; } = null!;

        /// <summary>Wall clock of the export-script generation on the loaded design.</summary>
        public TimeSpan ScriptGenerationElapsed { get; private set; }

        /// <summary>Loads the shipped example, assembles its logic network, generates its export script.</summary>
        public async Task InitializeAsync()
        {
            await Adder.InitializeAsync();
            var watch = Stopwatch.StartNew();
            NazcaScript = new SimpleNazcaExporter().Export(Canvas);
            watch.Stop();
            ScriptGenerationElapsed = watch.Elapsed;
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

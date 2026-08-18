using System.Text.Json;
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
/// Hard end-to-end scenario for the rung-4 → rung-7 chain (issue #995): the shipped
/// <c>examples/Logic Gate Full Adder.lun</c> (#990) must survive the manufacturing
/// path, not just load. One journey over real code paths, no mocks:
/// 1. Load the example through the real load path (pattern: #978's journey test).
/// 2. Assemble the logic network via <see cref="LogicNetworkAssembler"/> (#988) and
///    prove Sum/Cout — the corner combinations 000, 111 and 101 plus all others.
/// 3. Export the design to GDS through the real nazca export path (nazca-gated).
/// 4. Run the vendored CORNERSTONE SiN pre-DRC deck (#932's headless runner,
///    klayout-gated) over the exported GDS and pin the exact violation set — the
///    test fails when a new violation class appears or a count shifts.
/// Steps 3–4 skip cleanly without nazca/KLayout, like the existing GDS round-trips.
/// </summary>
public class FullAdderTapeoutJourneyTests : IClassFixture<FullAdderTapeoutJourneyFixture>
{
    // Pinned DRC baseline (rule → count), measured against KLayout 0.30.10: the export
    // is CLEAN — the deck registers all rule categories (incl. the 1 nm grid check for
    // every SiEPIC layer present) and reports zero items. The SiN 300nm layer rules
    // (203/204/…) are vacuous because the export carries SiEPIC layers. The empty set is
    // the strongest baseline: any future violation class — grid, spacing, feature — fails
    // this test until a human re-pins deliberately and files a follow-up.
    private static readonly IReadOnlyDictionary<string, int> PinnedViolations =
        new Dictionary<string, int>();

    private readonly FullAdderTapeoutJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public FullAdderTapeoutJourneyTests(FullAdderTapeoutJourneyFixture journey) => _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAsWiredGateGroups()
    {
        _journey.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the full adder contains only top-level gate groups");
        _journey.Groups.Count.ShouldBe(FullAdderTapeoutJourneyFixture.GateGroupCount);
        _journey.Canvas.Connections.Count.ShouldBe(FullAdderTapeoutJourneyFixture.WireCount);
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
            "every gate ships its persisted truth-table pin roles");
    }

    [Fact]
    public void Step2_AssembledNetwork_AllInputCombinations_YieldFullAdderSumAndCarry()
    {
        for (var pattern = 0; pattern < 8; pattern++)
        {
            var a = (pattern & 1) != 0;
            var b = (pattern & 2) != 0;
            var cin = (pattern & 4) != 0;

            var result = _journey.Network.Evaluate(_journey.InputBits(a, b, cin));

            result["H2SUM.Y"].ShouldBe(a ^ b ^ cin,
                $"Sum for A={a}, B={b}, Cin={cin} (corner combinations 000, 111, 101 included)");
            result["OROUT.Y"].ShouldBe((a && b) || (a && cin) || (b && cin),
                $"Cout = majority(A, B, Cin) for A={a}, B={b}, Cin={cin}");
        }
    }

    [SkippableFact]
    public async Task Step3_GdsExport_WritesDesignTopCellWithGeometry()
    {
        var python = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — the GDS export needs the real engine.");

        var gdsPath = await _journey.ExportToGdsAsync(python);

        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);

        library.TopCellCandidates.ShouldContain("ConnectAPIC_Design");
        var designCell = library.Cells["ConnectAPIC_Design"];
        designCell.Elements.OfType<GdsReference>().ShouldNotBeEmpty(
            "the gate groups are placed as cell references");
        designCell.Elements.OfType<GdsPolygon>().ShouldNotBeEmpty(
            "the routed waveguides flatten into real top-cell geometry");
    }

    [SkippableFact]
    public async Task Step4_CornerstoneDrc_RunnerCompletes_AndViolationSetMatchesPinnedBaseline()
    {
        var nazca = await GdsUserDesignFixture.FindNazcaPythonAsync();
        Skip.If(nazca == null, "No Python with nazca available — the GDS export needs the real engine.");
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");
        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null, "No KLayout on PATH/$KLAYOUT — the foundry-deck run needs the real engine.");

        var gdsPath = await _journey.ExportToGdsAsync(nazca);
        var reportPath = Path.Combine(_journey.WorkDirectory, "full-adder.lyrdb");
        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", reportPath, "--json");

        exitCode.ShouldBeOneOf(new[] { 0, 1 },
            $"the DRC runner must complete (exit 0 = clean, 1 = violations); a tool error (2) is a failure.\n" +
            $"stdout:\n{output}\nstderr:\n{error}");
        File.Exists(reportPath).ShouldBeTrue("the deck run must write the marker report");

        var violations = ParseViolationsByRule(output);
        violations.Keys.ShouldBe(PinnedViolations.Keys, ignoreOrder: true,
            customMessage: $"a new DRC violation class appeared or one vanished — " +
                $"update the pinned baseline deliberately.\nrunner output:\n{output}");
        foreach (var (rule, count) in violations)
            count.ShouldBe(PinnedViolations[rule], $"violation count for rule '{rule}'");
    }

    /// <summary>
    /// Parses the runner's <c>--json</c> summary into rule name → count. The JSON
    /// object is located from the first '{' so any non-JSON preamble the runner
    /// (or the tools it wraps) prints to stdout cannot break the parse.
    /// </summary>
    private static Dictionary<string, int> ParseViolationsByRule(string runnerOutput)
    {
        var jsonStart = runnerOutput.IndexOf('{');
        jsonStart.ShouldBeGreaterThanOrEqualTo(0,
            $"the runner's --json output must contain a JSON object.\nstdout:\n{runnerOutput}");
        using var doc = JsonDocument.Parse(runnerOutput[jsonStart..]);
        var result = new Dictionary<string, int>();
        foreach (var entry in doc.RootElement.GetProperty("violationsByRule").EnumerateObject())
            result[entry.Name] = entry.Value.GetInt32();
        return result;
    }
}

/// <summary>
/// Shared fixture for <see cref="FullAdderTapeoutJourneyTests"/>: loads the shipped
/// full-adder example once and assembles its logic network (each gate extraction is
/// a real simulation run), so every fact asserts one step of the same journey. The
/// GDS export is produced lazily by the gated steps and cached for reuse.
/// </summary>
public class FullAdderTapeoutJourneyFixture : IAsyncLifetime
{
    /// <summary>Gate groups in the shipped design (28 NAND + 4 NOT instances).</summary>
    public const int GateGroupCount = 32;

    /// <summary>Wires joining the gate groups in the shipped design.</summary>
    public const int WireCount = 30;

    private const string ExampleFileName = "Logic Gate Full Adder.lun";

    /// <summary>Network inputs driven by addend A (fan-out at the logic layer).</summary>
    private static readonly string[] InputsA =
    {
        "H1N1A1.A", "H1N1B1.A", "H1N21.A",
        "H1N1A2.A", "H1N1B2.A", "H1N22.A",
        "H1N1A3.A", "H1N1B3.A", "H1N23.A",
        "H1N1A4.A", "H1N1B4.A", "H1N24.A",
        "H1N5.A",
    };

    /// <summary>Network inputs driven by addend B (fan-out at the logic layer).</summary>
    private static readonly string[] InputsB =
    {
        "H1N1A1.B", "H1N1B1.B", "H1N31.B",
        "H1N1A2.B", "H1N1B2.B", "H1N32.B",
        "H1N1A3.B", "H1N1B3.B", "H1N33.B",
        "H1N1A4.B", "H1N1B4.B", "H1N34.B",
        "H1N5.B",
    };

    /// <summary>Network inputs driven by the carry-in (fan-out at the logic layer).</summary>
    private static readonly string[] InputsCin = { "H2N1A.A", "H2N1B.A", "H2N2.A", "H2N5.A" };

    private string? _exportedGdsPath;

    /// <summary>Temp working directory for the exported script, GDS and DRC report.</summary>
    public string WorkDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "full-adder-tapeout-" + Guid.NewGuid().ToString("N"));

    /// <summary>The canvas the shipped example loaded onto.</summary>
    public DesignCanvasViewModel Canvas { get; private set; } = null!;

    /// <summary>The loaded top-level gate groups.</summary>
    public List<ComponentGroup> Groups { get; private set; } = null!;

    /// <summary>The logic network assembled from the loaded design.</summary>
    public LogicNetworkEvaluator Network { get; private set; } = null!;

    /// <summary>Runs journey steps 1–2: load the example, assemble its logic network.</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WorkDirectory);
        var examplePath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
        Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(examplePath);
        Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
        Network = await LogicGateFullAdderExampleTests.AssembleNetwork(Canvas);
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

    /// <summary>The network input bits for one operand triple (A, B and Cin fan out at the logic layer).</summary>
    public Dictionary<string, bool> InputBits(bool a, bool b, bool cin)
    {
        var bits = new Dictionary<string, bool>();
        foreach (var name in InputsA) bits[name] = a;
        foreach (var name in InputsB) bits[name] = b;
        foreach (var name in InputsCin) bits[name] = cin;
        return bits;
    }

    /// <summary>
    /// Exports the loaded design through the real nazca export path (#978's pattern):
    /// <see cref="SimpleNazcaExporter"/> emits the script, the nazca-capable Python
    /// runs it, and the written GDS path is returned (cached across the gated steps).
    /// </summary>
    public async Task<string> ExportToGdsAsync(string nazcaPython)
    {
        if (_exportedGdsPath != null)
            return _exportedGdsPath;

        var script = new SimpleNazcaExporter().Export(Canvas);
        // The design cell is the GDS top cell, not a nazca wrapper.
        script.ShouldContain("nd.export_gds(topcells=[design]");

        var exportDir = Path.Combine(WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "full_adder.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(nazcaPython, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export failed:\n{run.StdOut}\n{run.StdErr}");

        _exportedGdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(_exportedGdsPath).ShouldBeTrue($"script did not write {_exportedGdsPath}:\n{run.StdOut}");
        return _exportedGdsPath;
    }
}

using CAP_Core.Export;
using Shouldly;
using UnitTests.Export;
using UnitTests.Routing;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// The end-artifact honest proof for the #704/#1078 overlap class (issue #1088):
/// the neighbouring-port geometry of <c>overlappingwaveguides.lun</c> (repro
/// constants shared with <see cref="Routing.Issue704ReproRoutingTests"/>) must
/// survive routing AND land in the exported GDS as one waveguide chain per routed
/// connection with zero overlap outside component footprints — the route-level
/// pinup of #1084 is only half the story foundries care about. The fixture
/// (<see cref="Issue704GdsExportHonestyJourneyFixture"/>) rebuilds the layout on
/// a real canvas, routes it like the app does and exports through the real nazca
/// path (same tooling probes and gating as the manufacturing journeys #995/#1036);
/// the produced GDS is read back with <c>scripts/extract_gds_coords.py</c> — whose
/// new <c>gdstk</c> fallback keeps the check executable on the repo's CI
/// toolchain even where <c>gdspy</c> is not installable.
/// </summary>
public class Issue704GdsExportHonestyTests
    : IClassFixture<Issue704GdsExportHonestyJourneyFixture>
{
    private const int ExpectedConnections = 2;
    private const string DesignCellName = "ConnectAPIC_Design";

    private readonly Issue704GdsExportHonestyJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public Issue704GdsExportHonestyTests(Issue704GdsExportHonestyJourneyFixture journey) =>
        _journey = journey;

    [Fact]
    public void Step1_BuildReproLayout_TwoNeighboringPortConnectionsOnCanvas()
    {
        _journey.Canvas.Components.Count.ShouldBe(3, "MZI_8, MZI_9 and Taper_5 carry the repro geometry");
        _journey.Canvas.Connections.Count.ShouldBe(ExpectedConnections,
            "MZI_8.o3 → MZI_9.o3 and Taper_5.o1 → MZI_9.o2 are the two neighbouring routes");
    }

    [Fact]
    public void Step2_Route_EveryConnectionReportsAValidPath()
    {
        foreach (var connectionVm in _journey.Canvas.Connections)
        {
            var connection = connectionVm.Connection;
            var description = ExportableConnections.Describe(connection.StartPin, connection.EndPin);
            connection.RoutedPath.ShouldNotBeNull($"route '{description}' produced no path at all");
            connection.RoutedPath.Segments.ShouldNotBeEmpty($"route '{description}' exported no segments");
            connection.RoutedPath.IsBlockedFallback.ShouldBeFalse(
                $"route '{description}' was left as an unresolved blocked fallback and must not export silently");
            connection.RoutedPath.IsInvalidGeometry.ShouldBeFalse(
                $"route '{description}' violates physical constraints and must fail here, not in the artifact");
        }
    }

    [Fact]
    public void Step3_NazcaExport_WritesTheDesignAsGdsTopCell()
    {
        _journey.NazcaScript.ShouldNotBeNullOrEmpty(
            "the real export path must produce a nazca script for the repro layout");
        _journey.NazcaScript.ShouldContain($"# Waveguide Connections",
            Case.Sensitive, "both routed connections must reach the exporter");
        _journey.NazcaScript.ShouldContain("nd.export_gds(topcells=[design]",
            Case.Sensitive, "the exported GDS carries the design as its top cell");
    }

    [Trait("Category", "Slow")]
    [SkippableFact]
    public async Task Step4_ExportedGdsWaveguides_NoOverlapAndNothingDropped()
    {
        var python = await FindFullToolchainPythonAsync();
        Skip.If(python == null, "No Python carrying nazca AND gdspy/gdstk — the export + extraction needs both.");

        var exportDir = Path.Combine(_journey.WorkDirectory, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "overlapping_waveguides_honesty.py");
        await File.WriteAllTextAsync(scriptPath, _journey.NazcaScript);
        var export = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        export.ExitCode.ShouldBe(0, $"the nazca export of the repro layout must succeed:\n{export.StdOut}\n{export.StdErr}");

        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"the export script must write {gdsPath}:\n{export.StdOut}");

        var extractor = new GdsCoordinateExtractor();
        extractor.SetCustomPythonPath(python);
        var extraction = await extractor.ExtractAsync(gdsPath);
        extraction.Success.ShouldBeTrue(
            $"extracting the exported GDS must succeed (gdspy/gdstk): {extraction.ErrorMessage}");
        extraction.JsonContent.ShouldNotBeNullOrEmpty("the extraction must emit structured coordinates");

        var violations = ExportedWaveguideOverlapAnalyzer.FindViolations(
            extraction.JsonContent!, DesignCellName, _journey.Connections, _journey.ComponentFootprints);
        violations.ShouldBeEmpty(
            "exported waveguides must keep exactly one chain per routed connection and never " +
            "overlap outside component footprints:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// Finds a Python that can run BOTH halves of the gated proof: the nazca export
    /// AND the coordinate extraction (gdspy, or the gdstk fallback the script gained
    /// for exactly this). Skips candidates that only import nazca so a bare PDK env
    /// never silently suppresses the extraction half.
    /// </summary>
    private static async Task<string?> FindFullToolchainPythonAsync()
    {
        foreach (var candidate in EnumerateCandidatePythons())
            if (await GdsUserDesignFixture.ProbeNazca(candidate) && await ProbeExtractionModule(candidate))
                return candidate;
        return null;
    }

    /// <summary>The managed-env and PATH pythons, mirroring <c>GdsUserDesignFixture</c>.</summary>
    private static IEnumerable<string> EnumerateCandidatePythons()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
            foreach (var root in Directory.GetDirectories(envs))
                foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
                {
                    var py = Path.Combine(root, rel);
                    if (File.Exists(py)) yield return py;
                }
        yield return "python";
        yield return "python3";
    }

    /// <summary>True when <paramref name="python"/> imports gdspy or gdstk.</summary>
    private static async Task<bool> ProbeExtractionModule(string python)
    {
        const string probe = "import importlib.util, sys; " +
            "sys.exit(0 if any(importlib.util.find_spec(m) for m in ('gdspy','gdstk')) else 1)";
        try
        {
            var result = await SiepicRealGeometryExportTests.RunPythonAsync(
                python, Path.GetTempPath(), "-c", probe);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

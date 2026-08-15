using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Export.CornerstoneDrc;

/// <summary>
/// Rung-7 foundry validation (issue #932): exported GDS, checked headless against the
/// vendored CORNERSTONE SiN 300nm KLayout pre-DRC deck via <c>scripts/run_cornerstone_drc.py</c>.
/// Both tests skip cleanly without KLayout (mirrors the nazca-gated GDS round-trips); the
/// export test additionally needs a Python with <c>cspdk.sin300</c>.
///
/// The broken-design test is the mechanical proof that our DRC-lite limits match the real
/// deck: a 0.2 µm gap on the SiN core layer must trip the deck's 250 nm gap rule — the same
/// value DRC-lite flags as min-waveguide-spacing.
/// </summary>
[Trait("Category", "Slow")]
public class CornerstoneDrcIntegrationTests : IDisposable
{
    private const int ExitClean = 0;
    private const int ExitViolations = 1;

    // Broken fixture: two 1100 nm wide core stripes (GDS 203) 200 nm apart — below the
    // deck's 250 nm gap rule but above its 250 nm min-feature rule, so exactly ONE rule trips.
    private const int GapNm = 200;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cornerstone-drc-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [SkippableFact]
    public async Task BrokenDesign_WaveguideGapBelowDeckMinimum_IsFlaggedByTheFoundryDeck()
    {
        var python = await ExternalToolProbes.FindPythonAsync();
        Skip.If(python == null, "No Python interpreter on PATH.");
        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null, "No KLayout on PATH/$KLAYOUT — foundry-deck proof needs the real engine.");

        var gdsPath = WriteBrokenTwoStripeGds();
        var reportPath = Path.Combine(_root, "broken.lyrdb");

        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            python, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", reportPath);

        exitCode.ShouldBe(ExitViolations, $"the 200 nm gap must trip the deck.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("1 x Minimum gap violation (GDS203 < 250nm)");
        // The stripes are 1100 nm wide — only the gap rule may trip, not the width rule.
        output.ShouldNotContain("Minimum feature size violation");
    }

    [SkippableFact]
    public async Task CleanExportedCornerstoneSinDesign_PassesTheFoundryDeck()
    {
        var cspdkPython = await ExternalToolProbes.FindCspdkPythonAsync();
        Skip.If(cspdkPython == null, "No Python with cspdk.sin300 — the export needs the real PDK backend.");
        var klayout = await ExternalToolProbes.FindKlayoutAsync();
        Skip.If(klayout == null, "No KLayout on PATH/$KLAYOUT — foundry-deck proof needs the real engine.");

        // Small demo design on the bundled CornerStone SiN PDK: mmi1x2 → straight,
        // one pin-to-pin waveguide (cross-section xs_nc, 1.2 µm wide on GDS 203).
        var canvas = CreateConnectedCornerstoneSinCanvas();
        var script = new GdsFactoryExporter().Export(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.StandaloneStubs));

        Directory.CreateDirectory(_root);
        var scriptPath = Path.Combine(_root, "design.py");
        var gdsPath = Path.Combine(_root, "design.gds");
        await File.WriteAllTextAsync(scriptPath, script);

        var export = await ExternalToolProbes.RunToolAsync(cspdkPython, scriptPath);
        export.exitCode.ShouldBe(0, $"export script must run cleanly.\nstderr:\n{export.error}");
        File.Exists(gdsPath).ShouldBeTrue("export produced no GDS");

        var (exitCode, output, error) = await ExternalToolProbes.RunToolAsync(
            cspdkPython, CornerstoneDrcPaths.RunnerScript, gdsPath,
            "--klayout", klayout, "--report", Path.Combine(_root, "clean.lyrdb"));

        exitCode.ShouldBe(ExitClean,
            $"the exported demo design must be foundry-clean.\nstdout:\n{output}\nstderr:\n{error}");
        output.ShouldContain("PASSED: 0 DRC violations.");
    }

    /// <summary>TOP with two 10 µm × 1.1 µm core stripes on (203,0), 200 nm apart vertically.</summary>
    private string WriteBrokenTwoStripeGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "broken.gds");
        const int stripeWidthNm = 1100;
        const int stripeLengthNm = 10_000;
        const int gapNm = GapNm;
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .Boundary(203, 0, (0, 0), (stripeLengthNm, 0),
                    (stripeLengthNm, stripeWidthNm), (0, stripeWidthNm), (0, 0))
                .Boundary(203, 0, (0, stripeWidthNm + gapNm), (stripeLengthNm, stripeWidthNm + gapNm),
                    (stripeLengthNm, 2 * stripeWidthNm + gapNm), (0, 2 * stripeWidthNm + gapNm),
                    (0, stripeWidthNm + gapNm))
            .EndCell()
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Same canvas shape as the cspdk export round-trip: two SiN cells, one connection.</summary>
    private static DesignCanvasViewModel CreateConnectedCornerstoneSinCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var mmi = CreateSinComponent("A", "cspdk.sin300.mmi1x2", 0, 0);
        var straight = CreateSinComponent("B", "cspdk.sin300.straight", 60, 0);
        canvas.AddComponent(mmi, "SiN A");
        canvas.AddComponent(straight, "SiN B");
        canvas.Connections.Add(new WaveguideConnectionViewModel(
            new CAP_Core.Components.Connections.WaveguideConnection
            {
                StartPin = mmi.PhysicalPins[0],
                EndPin = straight.PhysicalPins[0],
            }));
        return canvas;
    }

    private static CAP_Core.Components.Core.Component CreateSinComponent(
        string id, string gdsFactoryFunction, double x, double y)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = id;
        component.NazcaFunctionName = "";
        component.GdsFactoryFunction = gdsFactoryFunction;
        component.GdsFactoryRoutingCrossSection = "xs_nc";
        component.PhysicalX = x;
        component.PhysicalY = y;
        component.RotationDegrees = 0;
        component.PhysicalPins.Add(new CAP_Core.Components.Core.PhysicalPin
        {
            Name = "o1",
            ParentComponent = component,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 5,
            AngleDegrees = 180,
        });
        return component;
    }
}

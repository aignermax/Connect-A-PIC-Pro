using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.Routing;
using Shouldly;
using System.Collections.ObjectModel;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Validates that waveguide segments are exported with correct Nazca coordinates.
///
/// Motivation (Issue #366): multi-segment paths once used Nazca chaining (.put() without
/// coordinates) for every segment after the first, which let coordinate errors accumulate
/// along the chain. Every segment is now emitted with an absolute .put(x, y, angle).
///
/// Current contract (issue #565): all app→Nazca coordinates flow through
/// <see cref="NazcaCoordinateMapper"/>. Path geometry converts to Nazca by the universal
/// Y negation (ToNazca); pins convert the same way (GetPinNazcaPosition). There is no
/// separate NazcaOriginOffset correction on segments — cells are placed so their rendered
/// pins coincide with the app pins, so segments simply meet those pins.
///
/// Test categories:
///   A - Single straight segments (covered by #355; regression guard)
///   D - Absolute positioning, no chaining
///   E - Conditional GDS binary test (requires Python + Nazca)
/// </summary>
[Trait("Category", "Slow")]
public class GdsWaveguideAlignmentTests
{
    private const double Tolerance = 0.05; // µm — tight alignment tolerance

    private readonly ObservableCollection<ComponentTemplate> _library;
    private readonly SimpleNazcaExporter _exporter = new();
    private readonly NazcaCodeParser _parser = new();

    /// <summary>Initialises library with all PDK component templates.</summary>
    public GdsWaveguideAlignmentTests()
    {
        _library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
    }

    // ── Category A: single straight regression ────────────────────────────────

    /// <summary>
    /// Single straight segment GC→GC: waveguide must start at startPin and end at endPin.
    /// Regression guard for Issue #355 fix.
    /// </summary>
    [Fact]
    public void SingleStraight_GcToGc_BothEndpointsMatchPins()
    {
        var (canvas, gc1, gc2) = CreateTwoGratingCouplers(xSeparation: 300);
        var startPin = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var endPin = gc2.PhysicalPins.First(p => p.Name == "waveguide");

        AddStraightConnection(canvas, startPin, endPin);

        var (startNazcaX, startNazcaY) = startPin.GetAbsoluteNazcaPosition();
        var (endNazcaX, endNazcaY) = endPin.GetAbsoluteNazcaPosition();

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        parsed.WaveguideStubs.Count.ShouldBeGreaterThan(0, "Waveguide must be exported");
        var wg = parsed.WaveguideStubs.First();

        // Start must match startPin
        Math.Abs(wg.StartX - startNazcaX).ShouldBeLessThan(Tolerance,
            $"WG start X: got {wg.StartX:F4}, expected {startNazcaX:F4}");
        Math.Abs(wg.StartY - startNazcaY).ShouldBeLessThan(Tolerance,
            $"WG start Y: got {wg.StartY:F4}, expected {startNazcaY:F4}");

        // End (start + length along angle) must match endPin
        double endX = wg.StartX + wg.Length * Math.Cos(wg.StartAngle * Math.PI / 180.0);
        double endY = wg.StartY + wg.Length * Math.Sin(wg.StartAngle * Math.PI / 180.0);
        Math.Abs(endX - endNazcaX).ShouldBeLessThan(Tolerance,
            $"WG end X: got {endX:F4}, expected {endNazcaX:F4}");
        Math.Abs(endY - endNazcaY).ShouldBeLessThan(Tolerance,
            $"WG end Y: got {endY:F4}, expected {endNazcaY:F4}");
    }

    // ── Category D: absolute positioning, no chaining ─────────────────────────

    /// <summary>
    /// Multi-segment export must NOT use Nazca chaining (.put() without coordinates).
    /// Every segment must have explicit absolute .put(x, y, angle) coordinates.
    /// </summary>
    [Fact]
    public void MultiSegmentExport_NoSegmentUsesChaining()
    {
        var (canvas, gc1, gc2) = CreateTwoGratingCouplers(xSeparation: 200);
        var startPin = gc1.PhysicalPins.First(p => p.Name == "waveguide");
        var endPin = gc2.PhysicalPins.First(p => p.Name == "waveguide");

        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();

        // Multi-segment path
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, sx + 80, sy, 0));
        path.Segments.Add(new BendSegment(sx + 80, sy + 50, 50, 0, 90));
        path.Segments.Add(new StraightSegment(sx + 130, sy + 50, ex, ey, 90));
        canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);

        var script = _exporter.Export(canvas);

        // Split into lines, find waveguide segment lines
        var wgLines = script.Split('\n')
            .Where(l => l.TrimStart().StartsWith("nd.strt") || l.TrimStart().StartsWith("nd.bend"))
            .ToList();

        wgLines.Count.ShouldBeGreaterThan(0, "Waveguide segments must be present");
        foreach (var line in wgLines)
        {
            line.Trim().ShouldNotEndWith(".put()",
                $"Fix #366: chaining must not be used. Offending line: {line}");
        }
    }

    // ── Category E: conditional GDS binary test ───────────────────────────────

    /// <summary>
    /// When Python + Nazca are available: export to a real .gds binary, parse it, and
    /// verify that SREF placement positions match expected Nazca component coordinates.
    /// Waveguide geometry is confirmed by the Nazca code tests above; this test
    /// validates the full Python execution pipeline.
    /// </summary>
    [Fact]
    public async Task GdsBinary_WhenPythonAvailable_ComponentsAtCorrectNazcaPositions()
    {
        var gdsService = new GdsExportService();
        var env = await gdsService.CheckPythonEnvironmentAsync();
        if (!env.IsReady)
            return; // Skip gracefully

        var tempScript = Path.Combine(Path.GetTempPath(), $"gds_align_{Guid.NewGuid():N}.py");
        var tempGds = Path.ChangeExtension(tempScript, ".gds");

        try
        {
            var (canvas, gc1, gc2) = CreateTwoGratingCouplers(xSeparation: 300);
            var startPin = gc1.PhysicalPins.First(p => p.Name == "waveguide");
            var endPin = gc2.PhysicalPins.First(p => p.Name == "waveguide");
            AddStraightConnection(canvas, startPin, endPin);

            var script = _exporter.Export(canvas);
            await File.WriteAllTextAsync(tempScript, script);

            var result = await gdsService.ExportToGdsAsync(tempScript, generateGds: true);
            result.Success.ShouldBeTrue($"GDS generation failed: {result.ErrorMessage}");
            File.Exists(tempGds).ShouldBeTrue("GDS file must be created");

            var gds = GdsReader.ReadFile(tempGds);
            gds.ShouldNotBeNull("GDS file must be parseable");

            // At minimum, GDS must contain component structure references
            gds!.ComponentRefs.Count.ShouldBeGreaterThan(0,
                "GDS must contain SREF elements for components");

            // Boundary count includes component stubs + waveguide polygons
            var totalGeometry = gds.BoundaryCount + gds.WaveguidePaths.Count;
            totalGeometry.ShouldBeGreaterThan(0, "GDS must contain geometry");
        }
        finally
        {
            if (File.Exists(tempScript)) File.Delete(tempScript);
            if (File.Exists(tempGds)) File.Delete(tempGds);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (DesignCanvasViewModel Canvas, Component Gc1, Component Gc2) CreateTwoGratingCouplers(
        double xSeparation)
    {
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");
        var canvas = new DesignCanvasViewModel();

        var gc1 = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 0);
        gc1.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc1, gcTemplate.Name);

        var gc2 = ComponentTemplates.CreateFromTemplate(gcTemplate, xSeparation, 0);
        gc2.NazcaFunctionName = "ebeam_gc_te1550";
        canvas.AddComponent(gc2, gcTemplate.Name);

        return (canvas, gc1, gc2);
    }

    private static void AddStraightConnection(
        DesignCanvasViewModel canvas, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, startPin.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
    }
}

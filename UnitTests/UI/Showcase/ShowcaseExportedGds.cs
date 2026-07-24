using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.UI.Showcase;

/// <summary>
/// GDS-side plumbing for the canvas-vs-GDS showcase composite: locates a
/// gdsfactory-capable Python (the local ground-truth venv or a Lunima managed env),
/// runs the REAL exported script, extracts the produced GDS file's flattened polygons
/// via gdstk, and renders them as a dark layout pane in Lunima world coordinates
/// (Y-down, µm), so the pane can share the exact zoom/pan transform of a captured
/// canvas crop — same circuit, same scale, same position.
/// </summary>
internal static class ShowcaseExportedGds
{
    private const int PythonTimeoutMs = 300_000;

    /// <summary>GDS layer the export draws waveguides/silicon stubs on.</summary>
    private const int SiliconLayer = 1;

    /// <summary>GDS layer of electrical metal traces (<see cref="CAP_Core.Routing.MetalRouting.MetalRoutingSpec.DefaultMetalGdsLayer"/>).</summary>
    private const int MetalLayer = 11;

    /// <summary>GDS layer of metal-over-waveguide bridge markers.</summary>
    private const int BridgeLayer = 12;

    /// <summary>Dumps the design cell's flattened polygons (layer + points) as JSON.</summary>
    private const string ExtractSnippet =
        "import json, sys\n" +
        "import gdstk\n" +
        "lib = gdstk.read_gds(sys.argv[1])\n" +
        "named = [c for c in lib.cells if c.name == 'ConnectAPIC_Design']\n" +
        "top = named[0] if named else lib.top_level()[0]\n" +
        "out = [\n" +
        "    {'layer': p.layer, 'points': [[float(x), float(y)] for x, y in p.points]}\n" +
        "    for p in top.get_polygons(depth=None)\n" +
        "]\n" +
        "with open(sys.argv[2], 'w') as f:\n" +
        "    json.dump(out, f)\n";

    /// <summary>One flattened GDS polygon, vertices already converted to Lunima world
    /// coordinates (Y-down, µm): worldX = gdsX, worldY = -gdsY.</summary>
    public sealed record GdsPolygon(int Layer, IReadOnlyList<Point> WorldPoints);

    /// <summary>
    /// A Python that can run the gdsfactory export script: the local ground-truth venv
    /// (<c>%TEMP%/gf-groundtruth</c>) or a Lunima managed env. Null when none exists (CI)
    /// — the showcase asset is committed, so the test skips silently there.
    /// </summary>
    public static string? FindGdsFactoryPython()
    {
        var roots = new List<string> { Path.Combine(Path.GetTempPath(), "gf-groundtruth") };
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lunima", "envs");
        if (Directory.Exists(envs))
            roots.AddRange(Directory.GetDirectories(envs));

        return roots
            .SelectMany(root => new[]
            {
                Path.Combine(root, "Scripts", "python.exe"),
                Path.Combine(root, "bin", "python"),
            })
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Runs the exported gdsfactory script end to end (it writes <c>design.gds</c> next
    /// to itself) and returns the flattened polygons of the produced GDS in world
    /// coordinates. The temp work directory is removed afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<GdsPolygon>> RunAndExtractPolygonsAsync(
        string python, string exportScript)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lunima-canvas-vs-gds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "design.py");
            var gdsPath = Path.Combine(dir, "design.gds");
            var jsonPath = Path.Combine(dir, "polygons.json");
            var extractPath = Path.Combine(dir, "extract_polygons.py");
            await File.WriteAllTextAsync(scriptPath, exportScript);
            await File.WriteAllTextAsync(extractPath, ExtractSnippet);

            var (exportExit, _, exportErr) = await RunPythonAsync(python, scriptPath);
            exportExit.ShouldBe(0, $"the exported gdsfactory script must run cleanly.\nstderr:\n{exportErr}");
            File.Exists(gdsPath).ShouldBeTrue("the export script must write design.gds next to itself");

            var (extractExit, _, extractErr) = await RunPythonAsync(python, extractPath, gdsPath, jsonPath);
            extractExit.ShouldBe(0, $"the gdstk polygon extraction must run cleanly.\nstderr:\n{extractErr}");

            return ParsePolygons(await File.ReadAllTextAsync(jsonPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Renders the polygons as a dark layout pane: silicon in light waveguide gray,
    /// metal traces in gold — mirroring how the canvas colors the same circuit. The
    /// world→pixel transform (origin + zoom) is the caller's canvas-crop transform, so
    /// both panes coincide pixel for pixel. Thin waveguides (0.45 µm) keep a visible
    /// outline stroke at any zoom.
    /// </summary>
    public static RenderTargetBitmap RenderPane(
        IReadOnlyList<GdsPolygon> polygons, PixelSize paneSize,
        double originWorldX, double originWorldY, double zoom)
    {
        var pane = new RenderTargetBitmap(paneSize);
        using var ctx = pane.CreateDrawingContext();
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0A1020")),
            new Rect(0, 0, paneSize.Width, paneSize.Height));

        foreach (var polygon in polygons.OrderBy(p => p.Layer == MetalLayer || p.Layer == BridgeLayer))
        {
            var points = polygon.WorldPoints
                .Select(p => new Point((p.X - originWorldX) * zoom, (p.Y - originWorldY) * zoom))
                .ToList();
            points.Add(points[0]);
            var (fill, stroke) = StyleFor(polygon.Layer);
            ctx.DrawGeometry(fill, stroke, new PolylineGeometry(points, isFilled: true));
        }
        return pane;
    }

    /// <summary>
    /// Composes pane crops side by side (thin dark gutter) and stamps a small
    /// mono-font badge into each pane's top-left corner ("Canvas", "Exported GDS …").
    /// </summary>
    public static void ComposeLabeledSideBySide(
        string path, IReadOnlyList<(Bitmap Source, PixelRect Crop, string Label)> panes,
        int gutter = 8)
    {
        int width = panes.Sum(p => p.Crop.Width) + gutter * (panes.Count - 1);
        int height = panes.Max(p => p.Crop.Height);

        using var target = new RenderTargetBitmap(new PixelSize(width, height));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#05080F")),
                new Rect(0, 0, width, height));
            double x = 0;
            foreach (var (source, crop, label) in panes)
            {
                ctx.DrawImage(source,
                    new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                    new Rect(x, 0, crop.Width, crop.Height));
                DrawBadge(ctx, new Point(x + 18, 16), label);
                x += crop.Width + gutter;
            }
        }
        using var stream = new MemoryStream();
        target.Save(stream);
        ScreenshotArtifacts.WriteBytes(path, stream.ToArray());
    }

    private static void DrawBadge(DrawingContext ctx, Point origin, string label)
    {
        var text = new FormattedText(label, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas,Menlo,monospace"),
                FontStyle.Normal, FontWeight.SemiBold),
            26, new SolidColorBrush(Color.Parse("#E9EEF7")));
        var backing = new Rect(origin.X, origin.Y, text.Width + 30, text.Height + 16);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(215, 10, 16, 32)),
            new Pen(new SolidColorBrush(Color.Parse("#3A4A6B")), 1), backing, 7, 7);
        ctx.DrawText(text, new Point(origin.X + 15, origin.Y + 8));
    }

    /// <summary>Layer palette: silicon like the canvas' light waveguides, metal in the
    /// hero's golden trace color, bridges as translucent gold markers.</summary>
    private static (IBrush Fill, Pen Stroke) StyleFor(int layer) => layer switch
    {
        SiliconLayer => (
            new SolidColorBrush(Color.Parse("#D9E3F2")),
            new Pen(new SolidColorBrush(Color.Parse("#F2F7FF")), 1.4)),
        MetalLayer => (
            new SolidColorBrush(Color.Parse("#C9A44D")),
            new Pen(new SolidColorBrush(Color.Parse("#E5C97D")), 1.4)),
        BridgeLayer => (
            new SolidColorBrush(Color.FromArgb(90, 201, 164, 77)),
            new Pen(new SolidColorBrush(Color.Parse("#E5C97D")), 1.0)),
        _ => (
            new SolidColorBrush(Color.FromArgb(170, 107, 122, 148)),
            new Pen(new SolidColorBrush(Color.Parse("#8FA0BC")), 1.0)),
    };

    private static async Task<(int ExitCode, string Output, string Stderr)> RunPythonAsync(
        string python, params string[] args) =>
        await UvBootstrapper.RunProcessAsync(
            ProcessLaunchFactory.CreateDefault(), python, args,
            CancellationToken.None, timeoutMs: PythonTimeoutMs);

    private static IReadOnlyList<GdsPolygon> ParsePolygons(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<PolygonDto>>(json);
        dtos.ShouldNotBeNull("polygon JSON must parse");
        return dtos!
            .Select(d => new GdsPolygon(
                d.Layer,
                d.Points.Select(p => new Point(p[0], -p[1])).ToList()))
            .ToList();
    }

    private sealed record PolygonDto(
        [property: JsonPropertyName("layer")] int Layer,
        [property: JsonPropertyName("points")] double[][] Points);
}

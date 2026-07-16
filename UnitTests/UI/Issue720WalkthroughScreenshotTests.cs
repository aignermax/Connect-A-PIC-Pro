using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #720 (per-instance Nazca overrides carried through saved
/// group templates): replays the production save→reload→place→export flow and renders each
/// step as headless PNGs into <c>artifacts/ui-screenshots/issue-720/</c> plus a
/// <c>manifest.json</c> with one-sentence captions. Same Skia harness as
/// <see cref="UiScreenshotTests"/>; no production code is exercised differently than in
/// <c>PlaceGroupTemplateOverrideSeedingTests</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue720WalkthroughScreenshotTests : IDisposable
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;
    private const string RawCodeMarker = "nd.strt(length=123.5, width=0.45)";

    private readonly string _libraryPath =
        Path.Combine(Path.GetTempPath(), $"Issue720Walkthrough_{Guid.NewGuid():N}");

    public Issue720WalkthroughScreenshotTests() => Directory.CreateDirectory(_libraryPath);

    public void Dispose()
    {
        if (Directory.Exists(_libraryPath)) Directory.Delete(_libraryPath, true);
    }

    /// <summary>Renders the four walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public void CaptureIssue720Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png")) File.Delete(stale);
        var manifest = new List<ManifestEntry>();
        var window = new Window { Width = 940, Height = 460 };
        window.Show();

        // Step 1 — source design: a group whose first member has a raw-code override.
        var (group, overriddenId, sourceStore) =
            Issue720WalkthroughScenario.CreateGroupWithRawCodeOverride(RawCodeMarker);
        ShowFrame(window, "Source design — group with a per-instance Nazca override",
            Issue720GroupSceneRenderer.Render(group, new HashSet<string> { overriddenId }, 560, 360),
            InfoPanel(
                ("Override store (source design)", Brushes.LightGray),
                ($"  {Issue720GroupSceneRenderer.ShortId(overriddenId)}  →  raw-code override", Brushes.OrangeRed),
                (sourceStore[overriddenId].RawCode.TrimEnd(), Brushes.Gainsboro)));
        Capture(window, dir, "01-source-group-with-override.png",
            "The source design contains a group whose highlighted member carries a per-instance raw-code Nazca override in the design's override store.",
            manifest);

        // Step 2 — save to the group library with the new override-JSON provider, then
        // reload from disk in a fresh manager (simulates another design/session).
        new GroupLibraryManager(_libraryPath).SaveTemplate(group, "Ovr Template",
            nazcaOverrideJsonProvider: GroupTemplateNazcaOverrides.CreateJsonProvider(sourceStore));
        var loadManager = new GroupLibraryManager(_libraryPath);
        loadManager.LoadTemplates();
        var loaded = loadManager.Templates.Single(t => t.Name == "Ovr Template");
        var persistedJson = loaded.NazcaOverridesJson[overriddenId];
        ShowFrame(window, "Group library — template saved WITH its member override",
            InfoPanel(
                ($"Template \"{loaded.Name}\"   ({loaded.ComponentCount} members)", Brushes.White),
                ($"   file: {Path.GetFileName(loaded.FilePath)}", Brushes.Gray),
                ("Persisted NazcaOverridesJson (new in this PR):", Brushes.LightGray),
                ($"  \"{Issue720GroupSceneRenderer.ShortId(overriddenId)}\" :", Brushes.OrangeRed),
                (Excerpt(persistedJson, 8), Brushes.Gainsboro)),
            null);
        Capture(window, dir, "02-template-persists-override.png",
            "Saving the group to the design-independent library now persists the member's override JSON inside the template file, keyed by the template child identifier.",
            manifest);

        // Step 3 — place the reloaded template into an EMPTY design.
        var canvas = new DesignCanvasViewModel();
        var targetStore = new Dictionary<string, NazcaCodeOverride>();
        var cmd = PlaceGroupTemplateCommand.TryCreate(
            canvas, loadManager, loaded, 100, 100, targetStore);
        cmd.ShouldNotBeNull();
        cmd.Execute();
        var placedGroup = (ComponentGroup)canvas.Components[0].Component;
        var (seededId, seeded) = targetStore.Single();
        ShowFrame(window, "New empty design — template placed, override seeded",
            Issue720GroupSceneRenderer.Render(placedGroup, new HashSet<string> { seededId }, 560, 360),
            InfoPanel(
                ("Override store (target design)", Brushes.LightGray),
                ($"  template id: {Issue720GroupSceneRenderer.ShortId(overriddenId)}", Brushes.Gray),
                ($"  seeded as:   {Issue720GroupSceneRenderer.ShortId(seededId)}", Brushes.OrangeRed),
                ("(re-keyed to the placed instance's", Brushes.LightGray),
                (" new child identifier)", Brushes.LightGray)));
        Capture(window, dir, "03-placed-in-empty-design.png",
            "Placing the template in a fresh empty design seeds the override into that design's store under the new instance identifier — previously the store stayed empty.",
            manifest);

        // Step 4 — Nazca export of the new design contains the raw-code geometry.
        var script = new SimpleNazcaExporter().Export(canvas, overrides: targetStore);
        script.ShouldContain(RawCodeMarker);
        seeded.RawCode.ShouldContain(RawCodeMarker);
        ShowFrame(window, "Nazca export of the new design — raw-code geometry survives",
            ExportExcerptPanel(script), null);
        Capture(window, dir, "04-nazca-export-contains-raw-code.png",
            "The Nazca export of the new design emits the member's raw-code geometry (highlighted) instead of falling back to the default placeholder cell.",
            manifest);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        manifest.Count.ShouldBe(4);
    }

    // ── Frame composition ────────────────────────────────────────────────────

    /// <summary>Swaps the window content to a titled frame with a main and optional side area.</summary>
    private static void ShowFrame(Window window, string title, Control main, Control? side)
    {
        var body = new DockPanel { Margin = new Avalonia.Thickness(10) };
        var header = new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeight.Bold,
            Foreground = Brushes.White, Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        if (side != null)
        {
            side.Margin = new Avalonia.Thickness(12, 0, 0, 0);
            DockPanel.SetDock(side, Dock.Right);
            body.Children.Add(side);
        }
        body.Children.Add(main);
        window.Content = body;
        for (int i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Builds a left-aligned stack of colored mono-spaced text lines.</summary>
    private static Control InfoPanel(params (string Text, IBrush Brush)[] lines)
    {
        var panel = new StackPanel
        {
            Spacing = 4, Width = 330,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        foreach (var (text, brush) in lines)
            panel.Children.Add(new TextBlock
            {
                Text = text, Foreground = brush, FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    /// <summary>Renders the export-script lines around the raw-code marker, highlighted.</summary>
    private static Control ExportExcerptPanel(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        int hit = Array.FindIndex(lines, l => l.Contains(RawCodeMarker));
        int from = Math.Max(0, hit - 6);
        var panel = new StackPanel { Spacing = 1 };
        for (int i = from; i < Math.Min(lines.Length, hit + 7); i++)
            panel.Children.Add(new TextBlock
            {
                Text = lines[i].Length == 0 ? " " : lines[i],
                Foreground = lines[i].Contains(RawCodeMarker) || lines[i].Contains("nd.Cell")
                    ? Brushes.OrangeRed : Brushes.Gainsboro,
                FontSize = 13,
            });
        return panel;
    }

    /// <summary>
    /// Pretty-prints a persisted override-JSON payload for on-frame display, hiding
    /// null-valued properties and truncating to the first lines.
    /// </summary>
    private static string Excerpt(string json, int maxLines)
    {
        using var doc = JsonDocument.Parse(json);
        var pretty = JsonSerializer.Serialize(doc.RootElement,
            new JsonSerializerOptions { WriteIndented = true });
        var lines = pretty.Split('\n')
            .Where(l => !l.TrimEnd(',').EndsWith(": null"))
            .ToArray();
        return string.Join('\n', lines.Take(maxLines)) + (lines.Length > maxLines ? "\n  …" : "");
    }

    // ── Capture plumbing (same pattern as UiScreenshotTests) ─────────────────

    /// <summary>Captures the shown window to a PNG, fails on near-blank frames, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var bitmap = window.CaptureRenderedFrame();
        bitmap.ShouldNotBeNull($"CaptureRenderedFrame returned null for {filename}");
        var path = Path.Combine(dir, filename);
        int distinctColors;
        using (bitmap)
        {
            distinctColors = CountDistinctSampledColors(bitmap);
            bitmap.Save(path);
        }
        distinctColors.ShouldBeGreaterThan(MinDistinctSampledColors,
            $"Near-blank render — only {distinctColors} distinct sampled colors in {filename}.");
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Samples a grid of pixels and counts distinct ARGB values (blank-frame guard).</summary>
    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        if (fb.Size.Width <= 0 || fb.Size.Height <= 0) return 0;
        int stepX = Math.Max(1, fb.Size.Width / SampleGridSize);
        int stepY = Math.Max(1, fb.Size.Height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < fb.Size.Height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < fb.Size.Width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }

    /// <summary>Resolves the issue-720 output directory under the repo-root artifacts folder.</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir)) return Path.Combine(envDir, "issue-720");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0) dir = dir.Parent;
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "artifacts", "ui-screenshots", "issue-720");
    }

    /// <summary>One walkthrough manifest entry: PNG filename plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}

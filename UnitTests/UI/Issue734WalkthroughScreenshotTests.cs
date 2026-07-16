using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Views;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #734 ("Duplicate as custom PDK"): renders the one-step path from
/// a read-only bundled foundry PDK to an editable custom copy carrying the same process — the new
/// Duplicate button, the name prompt, the immediately-registered (and process-lock-enabled)
/// duplicate, and the per-PDK process editor adding a metal cross-section to the copy while the
/// foundry file stays byte-identical. Step-ordered PNGs plus <c>manifest.json</c> go to
/// <c>artifacts/ui-screenshots/issue-734/</c>. Same Skia harness as <see cref="UiScreenshotTests"/>;
/// the PDK Management rubric is baked into MainWindow.axaml (not headless-showable), so its rows
/// are re-created as a small code-built replica bound to the real <see cref="PdkManagerViewModel"/>
/// state; the process editor frames use the real <see cref="ProcessManagementWindow"/>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue734WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int MinDistinctColorsSparseFrame = 4;
    private const int SampleGridSize = 64;
    private const string SourcePdkName = "Demo PDK";
    private const string DuplicateName = "Demo PDK (custom)";

    /// <summary>Renders the six walkthrough steps and writes the manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue734Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var manifest = new List<ManifestEntry>();
        var storeRoot = Path.Combine(Path.GetTempPath(), "lunima-walk-734-" + Guid.NewGuid().ToString("N"));
        var prefsPath = Path.Combine(Path.GetTempPath(), "lunima-walk-734-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var leftPanel = BuildLeftPanel(prefsPath);
            var demoDraft = leftPanel.GetLoadedPdkDrafts().First(d => d.Name == SourcePdkName);
            leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
                "Demo SOI 220nm", ProcessFingerprintFactory.From(demoDraft),
                new List<string> { demoDraft.Name }, IsPlayground: false));
            var foundryBytesBefore = File.ReadAllBytes(demoDraft.FilePath!);

            CapturePdkManagementReplica(leftPanel.PdkManager, dir, "01-bundled-pdk-duplicate-button.png",
                "The bundled (read-only) Demo PDK row in PDK Management now offers a 'Duplicate…' "
                + "button where custom PDKs have 'Edit…'.", manifest);

            CaptureNamePrompt(dir, "02-name-prompt.png",
                "Clicking 'Duplicate…' asks for a name for the new custom PDK, prefilled with "
                + "'Demo PDK (custom)'.", manifest);

            var store = new UserPdkStore(storeRoot, new PdkJsonSaver(), new PdkLoader());
            var createdPath = BundledPdkDuplicationService.Duplicate(store, demoDraft, DuplicateName);
            var newDraft = leftPanel.RegisterCreatedCustomPdk(createdPath);
            newDraft.ShouldNotBeNull();

            CapturePdkManagementReplica(leftPanel.PdkManager, dir, "03-duplicate-registered-enabled.png",
                "The duplicate appears immediately as a user PDK with its own 'Edit…' button — "
                + "value-compatible with the foundry process, so it is enabled under the active "
                + "process lock.", manifest);

            var processVm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
            {
                PdkFilePathResolver = _ => createdPath,
            };
            processVm.LoadForSinglePdkEdit(newDraft!);
            var window = new ProcessManagementWindow
            {
                Width = 1280, Height = 640, DataContext = processVm,
                Title = $"Edit Process — {DuplicateName}",
            };
            window.Show();
            PumpRenderLoop();

            Capture(window, dir, "04-process-editor-on-duplicate.png",
                "The per-PDK process editor opens directly on the fresh duplicate, showing the "
                + "copied foundry layer stack, cross-sections and materials.", manifest);

            processVm.AddMetalXsectionCommand.Execute(null);
            PumpRenderLoop();

            Capture(window, dir, "05-metal-xsection-added.png",
                "'+ Metal (electrical)' adds a metal routing cross-section to the copy — the kind "
                + "of extension the read-only foundry PDK refuses.", manifest);

            await processVm.SaveProcessCommand.ExecuteAsync(null);
            PumpRenderLoop();

            Capture(window, dir, "06-saved-foundry-untouched.png",
                "Saving writes the extended process into the duplicate's own JSON only — the "
                + "bundled foundry file stays byte-identical.", manifest);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            File.ReadAllBytes(demoDraft.FilePath!).ShouldBe(foundryBytesBefore,
                "duplicating and editing the copy must never write to the foundry PDK's own file");
            new PdkLoader().LoadFromFileForEditing(createdPath).Process!
                .Xsections.ShouldContain(x => x.Kind == XsectionKind.Metal);
        }
        finally
        {
            if (Directory.Exists(storeRoot))
                Directory.Delete(storeRoot, true);
            if (File.Exists(prefsPath))
                File.Delete(prefsPath);
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

        manifest.Count.ShouldBe(6);
    }

    /// <summary>Builds the real LeftPanel exactly as the duplication acceptance tests do.</summary>
    private static LeftPanelViewModel BuildLeftPanel(string prefsPath)
    {
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, new PdkLoader(),
            new UserPreferencesService(prefsPath),
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        leftPanel.Initialize();
        return leftPanel;
    }

    /// <summary>
    /// Renders the PDK Management rows as a code-built replica of the MainWindow rubric (which
    /// cannot be shown headless), snapshotting the real <see cref="PdkManagerViewModel"/> state:
    /// checkbox with process lock, source badge, component count, and the per-row
    /// Edit…/Duplicate… button exactly as the visibility bindings resolve them.
    /// </summary>
    private static void CapturePdkManagementReplica(
        PdkManagerViewModel pdkManager, string dir, string filename, string caption,
        List<ManifestEntry> manifest)
    {
        var list = new StackPanel { Margin = new Avalonia.Thickness(10), Spacing = 3 };
        list.Children.Add(new TextBlock
        {
            Text = "PDK Management", FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        });
        list.Children.Add(new TextBlock
        {
            Text = pdkManager.StatusText, FontSize = 10, Foreground = Brushes.LightGray,
            Margin = new Avalonia.Thickness(0, 0, 0, 5),
        });
        foreach (var pdk in pdkManager.LoadedPdks)
        {
            var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            head.Children.Add(new CheckBox
            {
                IsChecked = pdk.IsEnabled, Content = pdk.Name, FontSize = 10,
                IsEnabled = !pdk.IsLockedByProcess, Foreground = Brushes.White,
            });
            if (pdk.IsLockedByProcess)
                head.Children.Add(new TextBlock
                {
                    Text = "[locked by process]", FontSize = 9, Foreground = Brushes.Gray,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            list.Children.Add(head);

            var sub = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Avalonia.Thickness(20, 0, 0, 0), Spacing = 5,
            };
            sub.Children.Add(new TextBlock { Text = pdk.SourceType, FontSize = 9, Foreground = Brushes.Gray });
            sub.Children.Add(new TextBlock { Text = $"{pdk.ComponentCount} components", FontSize = 9, Foreground = Brushes.Gray });
            sub.Children.Add(new Button
            {
                // Same rule as the IsVisible bindings in MainWindow.axaml (issue #734).
                Content = pdk.IsBundled ? "Duplicate…" : "Edit…",
                FontSize = 9, Padding = new Avalonia.Thickness(6, 1),
                Background = new SolidColorBrush(Color.Parse("#3d3d3d")), Foreground = Brushes.White,
            });
            list.Children.Add(sub);
        }

        ShowAndCapture(list, 380, 70 + pdkManager.LoadedPdks.Count * 58, dir, filename, caption, manifest);
    }

    /// <summary>
    /// Renders the "Duplicate as custom PDK" name prompt as the code-built
    /// <see cref="InputDialogService"/> lays it out (it needs a desktop lifetime, so it cannot be
    /// shown directly headless).
    /// </summary>
    private static void CaptureNamePrompt(
        string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Duplicate as custom PDK", FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Name for the new custom PDK", Foreground = Brushes.White,
            Margin = new Avalonia.Thickness(0, 10, 0, 5),
        });
        panel.Children.Add(new TextBox
        {
            Text = DuplicateName, Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3e3e3e")),
        });
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(new Button
        {
            Content = "OK", Width = 80, Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#0d6efd")),
        });
        buttons.Children.Add(new Button
        {
            Content = "Cancel", Width = 80, Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#3d3d3d")),
        });
        panel.Children.Add(buttons);

        ShowAndCapture(panel, 400, 190, dir, filename, caption, manifest);
    }

    /// <summary>Shows a code-built replica in a dark window, captures it (sparse color floor), closes it.</summary>
    private static void ShowAndCapture(
        Control content, int width, int height, string dir, string filename, string caption,
        List<ManifestEntry> manifest)
    {
        var window = new Window
        {
            Width = width, Height = height, Content = content,
            Background = new SolidColorBrush(Color.Parse("#2d2d2d")),
        };
        window.Show();
        PumpRenderLoop();
        Capture(window, dir, filename, caption, manifest, MinDistinctColorsSparseFrame);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the headless render timer and dispatcher so bindings paint before capture.</summary>
    private static void PumpRenderLoop()
    {
        for (int i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Captures the shown window to a PNG, fails on a near-blank frame, records the caption.</summary>
    private static void Capture(
        Window window, string dir, string filename, string caption, List<ManifestEntry> manifest,
        int minDistinctColors = MinDistinctSampledColors)
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

        distinctColors.ShouldBeGreaterThan(minDistinctColors,
            $"Near-blank render — only {distinctColors} distinct sampled colors in {filename}.");
        manifest.Add(new ManifestEntry(filename, caption));
    }

    /// <summary>Samples a grid of pixels and counts distinct ARGB values (blank-frame guard).</summary>
    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        int width = fb.Size.Width;
        int height = fb.Size.Height;
        if (width <= 0 || height <= 0) return 0;

        int stepX = Math.Max(1, width / SampleGridSize);
        int stepY = Math.Max(1, height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }

    /// <summary>Repo-root walkthrough output directory (env override: <c>UI_SHOT_DIR</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-734");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-734");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-734");
    }

    /// <summary>One manifest row: PNG file name plus its one-sentence caption.</summary>
    private sealed record ManifestEntry(string File, string Caption);
}

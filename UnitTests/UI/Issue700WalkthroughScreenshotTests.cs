using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Library;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #700 (user PDKs restored at startup): renders the left-panel
/// Component Library + PDK Management flow as step-ordered headless PNGs into
/// <c>artifacts/ui-screenshots/issue-700/</c> plus a <c>manifest.json</c> with captions.
/// The panel markup lives inline in MainWindow.axaml, whose Loaded wiring needs the
/// production DI container, so the steps render a faithful code-built replica of that
/// region bound to the REAL <see cref="MainViewModel"/> state (same Skia harness as
/// <see cref="UiScreenshotTests"/>).
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue700WalkthroughScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int SampleGridSize = 64;

    private const string MyLabPdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""MyLab User PDK"",
        ""components"": [
            {
                ""name"": ""MyLab Ring Resonator"", ""category"": ""Resonators"",
                ""nazcaFunction"": ""mylab.ring"",
                ""widthMicrometers"": 120, ""heightMicrometers"": 80,
                ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 40 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 120, ""offsetYMicrometers"": 40 }
                ]
            },
            {
                ""name"": ""MyLab MMI 2x2"", ""category"": ""Couplers"",
                ""nazcaFunction"": ""mylab.mmi22"",
                ""widthMicrometers"": 150, ""heightMicrometers"": 60,
                ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 15 },
                    { ""name"": ""a1"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 45 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 150, ""offsetYMicrometers"": 15 },
                    { ""name"": ""b1"", ""offsetXMicrometers"": 150, ""offsetYMicrometers"": 45 }
                ]
            },
            {
                ""name"": ""MyLab Taper"", ""category"": ""Waveguides"",
                ""nazcaFunction"": ""mylab.taper"",
                ""widthMicrometers"": 100, ""heightMicrometers"": 20,
                ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 10 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 10 }
                ]
            }
        ]
    }";

    /// <summary>Captures the three walkthrough steps and writes the caption manifest.</summary>
    [AvaloniaFact]
    public async Task CaptureIssue700Walkthrough()
    {
        var dir = ResolveOutputDirectory();
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.png"))
            File.Delete(stale);

        var prefsPath = Path.Combine(Path.GetTempPath(), $"issue700-prefs-{Guid.NewGuid()}.json");
        var pdkPath = Path.Combine(Path.GetTempPath(), $"issue700-demo-pdk-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(pdkPath, MyLabPdkJson);
            var manifest = new List<ManifestEntry>();

            // ---- Session 1: fresh preferences, before any user PDK import ----
            var vm1 = MainViewModelTestHelper.CreateMainViewModel(
                preferencesService: new UserPreferencesService(prefsPath));
            vm1.LeftPanel.SearchText = "MyLab";

            Capture(vm1, dir, "01-first-session-no-user-pdk.png",
                "First session: searching the component library for \"MyLab\" finds nothing and "
                + "PDK Management lists only the bundled PDKs — no user PDK is loaded yet.",
                manifest);

            // ---- Session 1: user imports the PDK via Tools → Load PDK… ----
            var dialog = new Mock<IFileDialogService>();
            dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(pdkPath);
            vm1.LeftPanel.FileDialogService = dialog.Object;
            await vm1.LeftPanel.LoadPdkCommand.ExecuteAsync(null);

            vm1.LeftPanel.PdkManager.IsPdkLoaded(pdkPath).ShouldBeTrue();
            Capture(vm1, dir, "02-user-pdk-imported.png",
                "The user imports \"MyLab User PDK\" via Tools → Load PDK…: its three components "
                + "appear in the library and the PDK is listed under PDK Management as 📂 User.",
                manifest);

            // ---- Session 2: app restart (new MainViewModel, same preferences file) ----
            var vm2 = MainViewModelTestHelper.CreateMainViewModel(
                preferencesService: new UserPreferencesService(prefsPath));
            vm2.LeftPanel.SearchText = "MyLab";

            vm2.LeftPanel.PdkManager.IsPdkLoaded(pdkPath).ShouldBeTrue();
            Capture(vm2, dir, "03-after-restart-pdk-restored.png",
                "After an app restart the PDK path recorded in preferences is reloaded at "
                + "startup, so the user PDK and its components are back without re-importing "
                + "(the fix for issue #700 — previously this view was empty again).",
                manifest);

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
            manifest.Count.ShouldBe(3);
        }
        finally
        {
            if (File.Exists(prefsPath)) File.Delete(prefsPath);
            if (File.Exists(pdkPath)) File.Delete(pdkPath);
        }
    }

    /// <summary>Builds the left-panel replica for the VM's current state, renders and saves it.</summary>
    private static void Capture(
        MainViewModel vm, string dir, string filename, string caption, List<ManifestEntry> manifest)
    {
        var window = new Window
        {
            Width = 360,
            Height = 640,
            Background = new SolidColorBrush(Color.Parse("#252526")),
            Content = BuildLeftPanelReplica(vm)
        };
        window.Show();
        for (int i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

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

    /// <summary>
    /// Mirrors the MainWindow left-panel region (search box, filtered library list, PDK
    /// Management rubric) bound to the real ViewModel collections this PR affects.
    /// </summary>
    private static Control BuildLeftPanelReplica(MainViewModel vm)
    {
        var dock = new DockPanel { Margin = new Avalonia.Thickness(10) };

        dock.Children.Add(Docked(Header("Component Library"), Dock.Top));
        dock.Children.Add(Docked(new TextBox
        {
            Text = vm.LeftPanel.SearchText,
            Watermark = "Search components...",
            FontSize = 11,
            Margin = new Avalonia.Thickness(0, 4)
        }, Dock.Top));

        var pdkSection = new StackPanel();
        pdkSection.Children.Add(Header("PDK Management"));
        pdkSection.Children.Add(Note(vm.LeftPanel.PdkManager.StatusText));
        var pdkList = new ItemsControl
        {
            ItemsSource = vm.LeftPanel.PdkManager.LoadedPdks,
            ItemTemplate = new FuncDataTemplate<PdkInfoViewModel>((pdk, _) => PdkRow(pdk))
        };
        pdkSection.Children.Add(pdkList);
        dock.Children.Add(Docked(pdkSection, Dock.Bottom));

        dock.Children.Add(new ListBox
        {
            ItemsSource = vm.LeftPanel.FilteredTemplates,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<ComponentTemplate>((t, _) => TemplateRow(t))
        });
        return dock;
    }

    private static Control PdkRow(PdkInfoViewModel pdk)
    {
        var stack = new StackPanel { Margin = new Avalonia.Thickness(0, 3) };
        stack.Children.Add(new CheckBox
        {
            IsChecked = pdk.IsEnabled,
            Content = pdk.Name,
            FontSize = 10,
            IsEnabled = !pdk.IsLockedByProcess
        });
        stack.Children.Add(Note($"{pdk.SourceBadge}   {pdk.ComponentCount} components",
            indent: 20));
        return stack;
    }

    private static Control TemplateRow(ComponentTemplate template)
    {
        var stack = new StackPanel { Margin = new Avalonia.Thickness(0, 1) };
        stack.Children.Add(new TextBlock
        {
            Text = template.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        stack.Children.Add(Note(template.Category));
        return stack;
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        Foreground = Brushes.White,
        Margin = new Avalonia.Thickness(0, 4)
    };

    private static TextBlock Note(string? text, double indent = 0) => new()
    {
        Text = text ?? "",
        FontSize = 9,
        Foreground = Brushes.Gray,
        Margin = new Avalonia.Thickness(indent, 0, 0, 2),
        TextWrapping = TextWrapping.Wrap
    };

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
            return Path.Combine(envDir, "issue-700");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-700");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-700");
    }

    private sealed record ManifestEntry(string File, string Caption);
}

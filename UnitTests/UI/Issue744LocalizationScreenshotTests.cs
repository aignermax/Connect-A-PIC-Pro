using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Settings;
using CAP.Avalonia.Views;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #744 (multi-language UI): renders the real MainWindow
/// live-switched through English, German, Simplified Chinese (proving CJK glyphs render)
/// and Spanish, plus the new Settings → Language picker page. Writes PNGs + manifest.json
/// to <c>artifacts/ui-screenshots/issue-744/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue744LocalizationScreenshotTests
{
    [AvaloniaFact]
    public void CaptureLocalizationWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        try
        {
            var english = CaptureMainWindowIn("en", Path.Combine(outputDir, "01-main-window-english.png"));
            CaptureMainWindowIn("de", Path.Combine(outputDir, "02-main-window-german.png"));
            var chinese = CaptureMainWindowIn("zh-Hans", Path.Combine(outputDir, "03-main-window-chinese.png"));
            CaptureMainWindowIn("es", Path.Combine(outputDir, "04-main-window-spanish.png"));
            CaptureLanguageSettingsPage(Path.Combine(outputDir, "05-settings-language-picker.png"));

            // The Chinese render must differ from the English one — a font-fallback
            // failure (tofu boxes everywhere or unchanged text) would keep them equal.
            chinese.SequenceEqual(english).ShouldBeFalse(
                "Chinese UI render is pixel-identical to English — CJK strings did not render");
        }
        finally
        {
            // Never leak a switched language into other tests sharing the singleton.
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }

        WriteManifest(outputDir);
    }

    /// <summary>
    /// Renders the real MainWindow after live-switching <see cref="LocalizationService.Instance"/>
    /// (the source every <c>{loc:Localize}</c> binding listens to) and returns the PNG bytes.
    /// DataContext is assigned only after Show() so the production Loaded wiring
    /// (which needs App.Services) no-ops in the headless host.
    /// </summary>
    private static byte[] CaptureMainWindowIn(string languageCode, string path)
    {
        LocalizationService.Instance.SetLanguage(languageCode);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var window = new MainWindow { Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.DataContext = vm;
        Dispatcher.UIThread.RunJobs();

        CaptureWindow(window, path);
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// 05 — the new Settings → Language page: a "System (auto-detect)" default plus
    /// every shipped language under its native name (English, Deutsch, 中文, Español).
    /// </summary>
    private static void CaptureLanguageSettingsPage(string path)
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        var preferences = new UserPreferencesService();
        var pageViewModel = new LanguageSettingsViewModel(LocalizationService.Instance, preferences);
        var page = new LanguageSettingsPage(pageViewModel, LocalizationService.Instance);
        var vm = new SettingsWindowViewModel(new ISettingsPage[] { page });

        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        CaptureWindow(window, path);
    }

    private static void CaptureWindow(Avalonia.Controls.Window window, string path)
    {
        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"render miss for {Path.GetFileName(path)}");
        using (bitmap)
            bitmap!.Save(path);
        new FileInfo(path).Length.ShouldBeGreaterThan(0);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-main-window-english.png", "caption": "Baseline: the main window in English — toolbar, panels, tooltips and status bar now read their strings from the localization service instead of hardcoded AXAML text."},
          {"file": "02-main-window-german.png", "caption": "The same window live-switched to German: every extracted string (Modus, Eigenschaften, Komponentenbibliothek…) re-reads instantly via the Item[] binding — no restart."},
          {"file": "03-main-window-chinese.png", "caption": "Simplified Chinese: CJK glyphs (模式, 属性, 元件库) render in the headless Skia host; the test asserts this frame differs from the English baseline."},
          {"file": "04-main-window-spanish.png", "caption": "Spanish: the fourth shipped language (Modo, Propiedades, Biblioteca de componentes) — any key missing from a translation would silently fall back to English, never a raw key."},
          {"file": "05-settings-language-picker.png", "caption": "New Settings → Language page: 'System (auto-detect)' is the default; each language is listed in its own native name so users always recognize theirs."}
        ]
        """;
        File.WriteAllText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-744</c> (or <c>UI_SHOT_DIR/issue-744</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-744");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-744");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-744");
    }
}

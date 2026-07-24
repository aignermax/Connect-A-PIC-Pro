using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(UnitTests.UI.ScreenshotTestAppBuilder))]

namespace UnitTests.UI;

/// <summary>
/// Minimal Avalonia application for headless screenshot tests.
/// Loads the Fluent theme plus the app-wide compact control height overrides —
/// intentionally avoids production App.cs DI setup so tests control all ViewModel
/// construction themselves, but still needs the production style include so the
/// screenshots reflect the real Button/ComboBox chrome.
/// </summary>
internal class ScreenshotTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://CAP.Avalonia/Styles/"))
        {
            Source = new Uri("avares://CAP.Avalonia/Styles/CompactControlHeights.axaml"),
        });
        // Mirrors the production App.axaml includes: without them AvaloniaEdit's TextEditor and
        // OxyPlot's PlotView have no control template, so UI-flow tests could not type into the
        // component code editor (issue #556 rationale in App.axaml applies here too).
        Styles.Add(new StyleInclude(new Uri("avares://CAP.Avalonia/Styles/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });
        Styles.Add(new StyleInclude(new Uri("avares://CAP.Avalonia/Styles/"))
        {
            Source = new Uri("avares://OxyPlot.Avalonia/Themes/Default.axaml"),
        });
    }
}

/// <summary>
/// Configures Avalonia with Skia rendering so that <c>CaptureRenderedFrame()</c> produces
/// real pixel data. UseHeadlessDrawing = false is required — true throws NotSupportedException.
/// Skia 11.3.13 is pinned in UnitTests.csproj to resolve the version conflict with
/// CAP.Avalonia's Avalonia.Skia 11.2.1 reference.
/// </summary>
public class ScreenshotTestAppBuilder
{
    /// <summary>Entry point called by <see cref="AvaloniaTestApplicationAttribute"/>.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ScreenshotTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

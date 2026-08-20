using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Analysis.AnalysisOutput;
using UnitTests.Helpers;
using UnitTests.UI.Flows;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render tests for the #907 "Help (?)" flyouts (round 2 of #881/#893): the
/// laser/light-source editor, the PDK Management rubric and the Transient analysis tab
/// each carry a <see cref="HelpFlyoutButton"/> whose flyout must open and show its
/// localized title. Every test runs under German so a missing translation falls back to
/// English and trips the not-English assertion; key parity across all five languages is
/// enforced separately by <c>LocalizationCompletenessTests</c>. Runs in the
/// parallelization-free LocalizationSingleton collection because it mutates the
/// process-wide language.
/// </summary>
[Collection("LocalizationSingleton")]
public class Issue907HelpFlyoutRenderTests
{
    private const string TestLanguage = "de";

    /// <summary>The laser editor's "?" opens a flyout titled "Laser / Lichtquelle".</summary>
    [AvaloniaFact]
    public void LaserHelpFlyout_OpensAndShowsLocalizedTitle()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var canvas = new DesignCanvasViewModel();
            var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 60, y: 60);
            coupler.Component.Identifier = "grating_coupler_1";
            coupler.Component.HumanReadableName = "Grating Coupler";

            var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
            canvas.SelectedComponent = coupler;
            // Same surface as Issue819LaserSpectrumScreenshotTests: the editor provider
            // is bypassed and the light-source editor is surfaced directly.
            vm.RightPanel.SelectedComponentEditor = new LightSourceEditorViewModel(coupler);

            var panel = new SelectedComponentPropertiesPanel { DataContext = vm };
            window = new Window
            {
                Width = 460,
                Height = 700,
                Content = new ScrollViewer { Content = panel },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the laser/light-source editor must carry the #907 help button");
            AssertFlyoutOpensWithTitle(window, help, "LaserHelp.Title", "Laser / light source");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>The Transient tab's "?" opens a flyout titled "Was macht der Transient-Modus?".</summary>
    [AvaloniaFact]
    public void TransientHelpFlyout_OpensAndShowsLocalizedTitle()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var panel = new TimeDomainPanel { DataContext = vm };
            window = new Window { Width = 900, Height = 500, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the Transient tab must carry the #907 help button");
            AssertFlyoutOpensWithTitle(window, help, "TransientHelp.Title", "What does Transient mode do?");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>The PDK Management rubric's "?" opens a flyout titled "PDKs und Prozesse".</summary>
    [AvaloniaFact]
    // Boots the real MainWindow — too heavy for local default runs (CI covers it,
    // the local runners exclude Category=Slow); same trade-off as the UiFlow tests.
    [Trait("Category", "UiFlows")]
    [Trait("Category", "Slow")]
    public void PdkHelpFlyout_OpensAndShowsLocalizedTitle()
    {
        using var host = new UiFlowTestHost();
        try
        {
            // The host pins English at construction; switch to German and let the
            // {loc:Localize} bindings re-read before looking the button up by title.
            LocalizationService.Instance.SetLanguage(TestLanguage);
            UiInput.RunJobs();

            var expected = LocalizationService.Instance.Translate("PdkHelp.Title");
            var help = UiInput.Descendants<HelpFlyoutButton>(host.Window)
                .FirstOrDefault(h => h.Title == expected);
            help.ShouldNotBeNull("the PDK Management rubric must carry the #907 help button");
            AssertFlyoutOpensWithTitle(host.Window, help, "PdkHelp.Title", "PDKs and processes");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
            UiInput.RunJobs();
        }
    }

    /// <summary>
    /// Proves the flyout opens: the localized title is absent from the window's visual
    /// tree before <c>ShowAt</c> and present after (the flyout's header TextBlock).
    /// <paramref name="englishTitle"/> guards against silently falling back to English.
    /// </summary>
    private static void AssertFlyoutOpensWithTitle(
        Window window, HelpFlyoutButton help, string titleKey, string englishTitle)
    {
        var expected = LocalizationService.Instance.Translate(titleKey);
        expected.ShouldNotBe(englishTitle,
            $"test language must translate {titleKey} — an English value means a missing key");

        window.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Text == expected)
            .ShouldBeFalse($"the flyout title '{expected}' must not render before the flyout opens");

        var innerButton = help.GetVisualDescendants().OfType<Button>().First();
        innerButton.Flyout.ShouldNotBeNull("the help button must host a flyout");
        innerButton.Flyout!.ShowAt(innerButton);
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Text == expected)
            .ShouldBeTrue($"opening the flyout must show the localized title '{expected}'");
    }
}

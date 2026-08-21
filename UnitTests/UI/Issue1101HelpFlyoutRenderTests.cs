using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the #1101 education slice (#881 family): the
/// Truth Table help flyout explains that every row is a real S-matrix simulation, and the
/// Logic panel help flyout explains registers and clocking. #1118 refreshed the register
/// copy to name what shipped alongside it — the Truth Table panel's register toggle, the
/// Logic panel's Step clock button, and the SR-latch / 2-bit-counter examples. #1134
/// adds the Run auto-clock (#1111) and Reset (#1127) section to the same flyout — one
/// tick per Step press, cadence as UI convenience, power-up reset as a behavioral
/// convention. Same pattern as <c>Issue928HelpFlyoutRenderTests</c>: the render tests
/// run under German so a missing translation falls back to English and trips the
/// not-English assertion. Runs in the parallelization-free LocalizationSingleton
/// collection because it mutates the process-wide language.
/// </summary>
[Collection("LocalizationSingleton")]
public class Issue1101HelpFlyoutRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "TruthTableHelp.SimulationTitle",
        "TruthTableHelp.SimulationBody",
        "LogicPanelHelp.RegisterTitle",
        "LogicPanelHelp.RegisterBody",
        "LogicPanelHelp.RunResetTitle",
        "LogicPanelHelp.RunResetBody",
    };

    /// <summary>The Truth Table "?" shows the new real-simulation section (German title).</summary>
    [AvaloniaFact]
    public void TruthTableHelpFlyout_ShowsRealSimulationSection()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var panel = new TruthTablePanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the Truth Table panel must carry the help button");
            AssertFlyoutShows(window, help, "TruthTableHelp.SimulationTitle", "Every row is a real optical simulation");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>The Logic panel header "?" shows the new registers/clocking section.</summary>
    [AvaloniaFact]
    public void LogicPanelHelpFlyout_ShowsRegisterSection()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the Logic panel header must carry the help button");
            AssertFlyoutShows(window, help, "LogicPanelHelp.RegisterTitle", "Registers and clocking");
            AssertFlyoutShows(window, help, "LogicPanelHelp.RunResetTitle", "Run and Reset");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new #1101 key exists with a non-empty value in all five shipped languages,
    /// and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewHelpKeys_ExistAndAreTranslatedInAllFiveLanguages()
    {
        var english = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);
        foreach (var key in NewKeys)
            english.ContainsKey(key).ShouldBeTrue($"English must define {key}");

        foreach (var language in SupportedLanguage.All)
        {
            var table = LocalizationResourceLoader.Load(language.Code);
            foreach (var key in NewKeys)
            {
                table.ContainsKey(key).ShouldBeTrue($"{language.Code} must define {key}");
                table[key].ShouldNotBeNullOrWhiteSpace($"{language.Code} must translate {key}");
                if (language != SupportedLanguage.English)
                    table[key].ShouldNotBe(english[key],
                        $"{language.Code} must not fall back to English for {key}");
            }
        }
    }

    /// <summary>
    /// Pins the physics-honesty sentences: the truth-table section must credit the real
    /// S-matrix simulation, and the register section must mark the power-up-cleared state
    /// as a behavioral convention and tie feedback loops to registers. Since #1118 the
    /// register section must also name the shipped UI (register toggle, Step clock
    /// button) and point at the SR-latch / 2-bit-counter examples as the try-it-now path.
    /// </summary>
    [Fact]
    public void NewHelpText_KeepsPlainLanguageAndPhysicsHonesty()
    {
        var en = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);

        en["TruthTableHelp.SimulationBody"].ShouldContain("S-matrix");
        en["TruthTableHelp.SimulationBody"].ShouldContain("2^N");

        en["LogicPanelHelp.RegisterBody"].ShouldContain("behavioral");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("feedback");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("0");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("Register (state element)");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("Step clock");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("SR-Latch");
        en["LogicPanelHelp.RegisterBody"].ShouldContain("Counter 2-bit");

        // Run cadence honesty + reset-as-convention (#1134): the flyout ties the
        // tick to a Step, marks the cadence as a UI convenience, and names Reset's
        // power-up convention a behavioral-model claim, not physics.
        en["LogicPanelHelp.RunResetBody"].ShouldContain("Step");
        en["LogicPanelHelp.RunResetBody"].ShouldContain("not physics");
        en["LogicPanelHelp.RunResetBody"].ShouldContain("behavioral model");
        en["LogicPanelHelp.RunResetBody"].ShouldContain("not a physical claim");
        en["LogicPanelHelp.RunResetBody"].ShouldContain("restarts at 0");
    }

    /// <summary>
    /// Opens the help flyout and asserts the localized section title appears in the
    /// window's visual tree. <paramref name="englishTitle"/> guards against silently
    /// falling back to English in the German test language.
    /// </summary>
    private static void AssertFlyoutShows(
        Window window, HelpFlyoutButton help, string titleKey, string englishTitle)
    {
        var expected = LocalizationService.Instance.Translate(titleKey);
        expected.ShouldNotBe(englishTitle,
            $"test language must translate {titleKey} — an English value means a missing key");

        var innerButton = help.GetVisualDescendants().OfType<Button>().First();
        innerButton.Flyout.ShouldNotBeNull("the help button must host a flyout");
        innerButton.Flyout!.ShowAt(innerButton);
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Text == expected)
            .ShouldBeTrue($"opening the flyout must show the localized title '{expected}'");
    }
}

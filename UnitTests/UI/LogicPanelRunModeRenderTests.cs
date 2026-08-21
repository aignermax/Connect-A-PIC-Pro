using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the Logic panel's Run (auto-clock) mode
/// (issue #1111): the Run button and the interval selector render next to Step when
/// the network has registers, the button label toggles to Stop while the auto-clock
/// runs, and every new string is translated in all five shipped languages. Same
/// pattern as <c>LogicPanelPlaybackRenderTests</c>: the render tests run under
/// German so a missing translation falls back to English and trips the assertion.
/// </summary>
[Collection("LocalizationSingleton")]
public class LogicPanelRunModeRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "LogicPanel.Run",
        "LogicPanel.RunStop",
        "LogicPanel.RunIntervalFormat",
    };

    /// <summary>The register row renders a Run button with its localized label and the interval selector.</summary>
    [AvaloniaFact]
    public void RegisterRow_AtRest_RendersRunButtonAndIntervalSelector()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            logic.HasNetwork = true;
            logic.HasRegisters = true;

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            logic.IsRunning.ShouldBeFalse();
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.Run")))
                .ShouldBeTrue("the Run button renders with its localized label");

            var selector = panel.GetVisualDescendants().OfType<ComboBox>().SingleOrDefault();
            selector.ShouldNotBeNull("the interval selector renders next to Run");
            selector.Items.Count.ShouldBe(3);
            ((LogicRunIntervalOption)selector.SelectedItem!).Label
                .ShouldBe("1 s pro Takt", "the default cadence shows its localized label");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>While the auto-clock runs the button shows Stop; stopping flips it back to Run.</summary>
    [AvaloniaFact]
    public void RegisterRow_WhileRunning_RendersStopLabel()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            logic.HasNetwork = true;
            logic.HasRegisters = true;

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            logic.IsRunning = true;
            Dispatcher.UIThread.RunJobs();

            logic.RunStopText.ShouldBe(LocalizationService.Instance.Translate("LogicPanel.RunStop"));
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.RunStop")))
                .ShouldBeTrue("the button toggles to the localized Stop label while running");

            logic.IsRunning = false;
            Dispatcher.UIThread.RunJobs();

            logic.RunStopText.ShouldBe(LocalizationService.Instance.Translate("LogicPanel.Run"));
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new Run-mode key exists with a non-empty value in all five shipped
    /// languages, and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewRunKeys_ExistAndAreTranslatedInAllFiveLanguages()
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
}

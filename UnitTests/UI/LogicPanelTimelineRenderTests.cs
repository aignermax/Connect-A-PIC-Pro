using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the Logic panel's event timeline
/// (issue #1045, rung 5 visualizer slice 2): the Timeline section renders its
/// header and "(?)" help flyout, switch-event rows show time, gate.pin, and
/// transition, the empty-state hint shows when no toggle has happened, and
/// every new string is translated in all five shipped languages. Same pattern
/// as <c>TruthTablePanelRenderTests</c>: the render tests run under German so
/// a missing translation falls back to English and trips the assertion.
/// </summary>
[Collection("LocalizationSingleton")]
public class LogicPanelTimelineRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "LogicPanel.Timeline",
        "LogicPanel.TimelineEmpty",
        "LogicPanelTimelineHelp.Title",
        "LogicPanelTimelineHelp.Intro",
        "LogicPanelTimelineHelp.PhysicsTitle",
        "LogicPanelTimelineHelp.PhysicsBody",
        "LogicPanelTimelineHelp.OrderTitle",
        "LogicPanelTimelineHelp.OrderBody",
    };

    /// <summary>The Timeline section's "?" opens a flyout with the localized title.</summary>
    [AvaloniaFact]
    public void TimelineHelpFlyout_OpensAndShowsLocalizedTitle()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            vm.RightPanel.Logic.HasNetwork = true;
            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var helpButtons = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().ToList();
            var timelineHelp = helpButtons.FirstOrDefault(h =>
                h.Title == LocalizationService.Instance.Translate("LogicPanelTimelineHelp.Title"));
            timelineHelp.ShouldNotBeNull("the Timeline section must carry its help button");

            var expected = LocalizationService.Instance.Translate("LogicPanelTimelineHelp.Title");
            expected.ShouldNotBe("Why do the events ripple?",
                "test language must translate LogicPanelTimelineHelp.Title — an English value means a missing key");

            var innerButton = timelineHelp.GetVisualDescendants().OfType<Button>().First();
            innerButton.Flyout.ShouldNotBeNull("the help button must host a flyout");
            innerButton.Flyout!.ShowAt(innerButton);
            Dispatcher.UIThread.RunJobs();

            window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == expected)
                .ShouldBeTrue($"opening the flyout must show the localized title '{expected}'");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>With events populated the timeline rows render time, gate.pin, and transition.</summary>
    [AvaloniaFact]
    public void TimelineSection_WithEvents_RendersRows()
    {
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            logic.HasNetwork = true;
            logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
                new LogicSwitchEvent(12.3, "H1SUM1", "Y", true)));
            logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
                new LogicSwitchEvent(25.7, "H2SUM", "Y", false)));
            logic.HasTimelineEvents = true;

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textBlocks = panel.GetVisualDescendants().OfType<TextBlock>().ToList();
            // The times are formatted in the machine's current culture ("12,3 ps" under
            // de-DE), so assert against the rows' own display text, not a hardcoded literal.
            textBlocks.Any(t => t.Text == logic.TimelineEvents[0].TimeText).ShouldBeTrue("the first row shows its time");
            textBlocks.Any(t => t.Text == "H1SUM1.Y").ShouldBeTrue("the first row shows gate.pin");
            textBlocks.Any(t => t.Text == "0→1").ShouldBeTrue("the first row shows a rising transition");
            textBlocks.Any(t => t.Text == logic.TimelineEvents[1].TimeText).ShouldBeTrue("the second row shows its time");
            textBlocks.Any(t => t.Text == "1→0").ShouldBeTrue("the second row shows a falling transition");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Without events the empty-state hint renders instead of rows.</summary>
    [AvaloniaFact]
    public void TimelineSection_NoEvents_ShowsEmptyHint()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            logic.HasNetwork = true;
            logic.HasTimelineEvents = false;

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var hint = LocalizationService.Instance.Translate("LogicPanel.TimelineEmpty");
            panel.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == hint)
                .ShouldBeTrue("the empty state must show the translated hint");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new timeline key exists with a non-empty value in all five shipped languages,
    /// and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewTimelineKeys_ExistAndAreTranslatedInAllFiveLanguages()
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

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Analysis.LogicAnalysis;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the Logic panel's timeline replay
/// (issue #1058, rung 5 visualizer slice 3): with a timeline event selected, the
/// replayed row renders highlighted and the localized "showing t = X ps" line and
/// the Prev/Next/exit buttons render, and every new string is translated in all five
/// shipped languages. Same pattern as <c>LogicPanelTimelineRenderTests</c>: the render
/// tests run under German so a missing translation falls back to English and trips
/// the assertion.
/// </summary>
[Collection("LocalizationSingleton")]
public class LogicPanelReplayRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "LogicPanel.ReplayPrev",
        "LogicPanel.ReplayNext",
        "LogicPanel.ReplayTime",
        "LogicPanel.ReplayExit",
        "LogicPanelTimelineHelp.ReplayTitle",
        "LogicPanelTimelineHelp.ReplayBody",
    };

    /// <summary>With a timeline event selected, its row renders highlighted.</summary>
    [AvaloniaFact]
    public void TimelineSection_EventSelected_RendersHighlightedRow()
    {
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            PopulateTimeline(logic);
            logic.SelectedTimelineEvent = logic.TimelineEvents[1];

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rows = panel.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("timeline-event"))
                .ToList();
            rows.Count.ShouldBe(2, "every timeline row is a clickable replay button");
            rows.Count(b => b.Classes.Contains("selected")).ShouldBe(1,
                "exactly the replayed row carries the highlight");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Replay active: the "showing t = X ps" line and the navigation buttons render.</summary>
    [AvaloniaFact]
    public void TimelineSection_ReplayActive_RendersTimeLineAndNavigationButtons()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            PopulateTimeline(logic);
            logic.SelectedTimelineEvent = logic.TimelineEvents[0];

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            logic.ReplayTimeText.ShouldNotBeNullOrEmpty(
                "selecting an event announces the replayed instant");
            panel.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == logic.ReplayTimeText)
                .ShouldBeTrue($"the panel must render the replay line '{logic.ReplayTimeText}'");

            var buttons = panel.GetVisualDescendants().OfType<Button>().ToList();
            buttons.Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ReplayPrev")))
                .ShouldBeTrue("the Prev button renders with its localized label");
            buttons.Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ReplayNext")))
                .ShouldBeTrue("the Next button renders with its localized label");
            buttons.Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ReplayExit")))
                .ShouldBeTrue("the 'back to live' button renders while replay is active");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>No selection: no row is highlighted and the exit button stays hidden.</summary>
    [AvaloniaFact]
    public void TimelineSection_NoSelection_HidesReplayLineAndExitButton()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            PopulateTimeline(logic);

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            logic.IsReplayActive.ShouldBeFalse();
            panel.GetVisualDescendants().OfType<Button>()
                .Where(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ReplayExit")))
                .ShouldAllBe(b => !b.IsEffectivelyVisible,
                    "without a selection the 'back to live' button stays hidden");
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Classes.Contains("selected"))
                .ShouldBeFalse("without a selection no row is highlighted");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new replay key exists with a non-empty value in all five shipped languages,
    /// and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewReplayKeys_ExistAndAreTranslatedInAllFiveLanguages()
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

    /// <summary>Fills the panel's timeline with two switch events, like a real toggle would.</summary>
    private static void PopulateTimeline(LogicPanelViewModel logic)
    {
        logic.HasNetwork = true;
        logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
            new LogicSwitchEvent(12.3, "H1SUM1", "Y", true)));
        logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
            new LogicSwitchEvent(25.7, "H2SUM", "Y", false)));
        logic.HasTimelineEvents = true;
    }
}

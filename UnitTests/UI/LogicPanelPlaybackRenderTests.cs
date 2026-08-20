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
/// Headless render and resource tests for the Logic panel's timeline auto-play
/// (issue #1069, rung 5 visualizer slice 4): the Play button renders in the replay
/// bar, its label toggles to Pause while the ripple runs, and every new string is
/// translated in all five shipped languages. Same pattern as
/// <c>LogicPanelReplayRenderTests</c>: the render tests run under German so a
/// missing translation falls back to English and trips the assertion. The timeline
/// gets three rows so a stray DispatcherTimer tick cannot end playback mid-assert.
/// </summary>
[Collection("LocalizationSingleton")]
public class LogicPanelPlaybackRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "LogicPanel.PlaybackPlay",
        "LogicPanel.PlaybackPause",
        "LogicPanelTimelineHelp.PlaybackBody",
    };

    /// <summary>The replay bar renders a Play button with its localized label.</summary>
    [AvaloniaFact]
    public void ReplayBar_AtRest_RendersPlayButton()
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

            logic.IsPlaying.ShouldBeFalse();
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.PlaybackPlay")))
                .ShouldBeTrue("the Play button renders with its localized label");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>While playing the button shows Pause; pausing flips it back to Play.</summary>
    [AvaloniaFact]
    public void ReplayBar_WhilePlaying_RendersPauseLabel()
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

            logic.TogglePlaybackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            logic.IsPlaying.ShouldBeTrue();
            logic.PlayPauseText.ShouldBe(LocalizationService.Instance.Translate("LogicPanel.PlaybackPause"));
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.PlaybackPause")))
                .ShouldBeTrue("the button toggles to the localized Pause label while playing");

            logic.TogglePlaybackCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            logic.IsPlaying.ShouldBeFalse("toggling again pauses the ripple");
            logic.PlayPauseText.ShouldBe(LocalizationService.Instance.Translate("LogicPanel.PlaybackPlay"));
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new auto-play key exists with a non-empty value in all five shipped
    /// languages, and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewPlaybackKeys_ExistAndAreTranslatedInAllFiveLanguages()
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

    /// <summary>Fills the panel's timeline with three switch events, like a real toggle would.</summary>
    private static void PopulateTimeline(LogicPanelViewModel logic)
    {
        logic.HasNetwork = true;
        logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
            new LogicSwitchEvent(12.3, "H1SUM1", "Y", true)));
        logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
            new LogicSwitchEvent(25.7, "H2SUM", "Y", false)));
        logic.TimelineEvents.Add(new LogicTimelineEventViewModel(
            new LogicSwitchEvent(40.1, "COUT", "Y", true)));
        logic.HasTimelineEvents = true;
    }
}

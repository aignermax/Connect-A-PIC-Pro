using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the Logic panel's Reset button (issue
/// #1127, rung 5 visualizer): the button renders next to Step with its localized
/// label when the network contains registers, stays hidden for a purely
/// combinational network, and the new string is translated in all five shipped
/// languages. Same pattern as <c>LogicPanelReplayRenderTests</c>: the render tests
/// run under German so a missing translation falls back to English and trips the
/// assertion.
/// </summary>
[Collection("LocalizationSingleton")]
public class LogicPanelResetRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "LogicPanel.ResetRegisters",
    };

    /// <summary>A register network renders the Reset button next to Step.</summary>
    [AvaloniaFact]
    public void RegistersSection_RegisterNetwork_RendersResetButton()
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

            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => b.IsEffectivelyVisible
                          && Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ResetRegisters")))
                .ShouldBeTrue("the Reset button renders with its localized label");
            panel.GetVisualDescendants().OfType<Button>()
                .Any(b => b.IsEffectivelyVisible
                          && Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.StepClock")))
                .ShouldBeTrue("the Step button renders beside it");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>A purely combinational network never shows the Reset button.</summary>
    [AvaloniaFact]
    public void RegistersSection_CombinationalNetwork_HidesResetButton()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var logic = vm.RightPanel.Logic;
            logic.HasNetwork = true;
            logic.HasRegisters.ShouldBeFalse("no register was designated");

            var panel = new LogicPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            panel.GetVisualDescendants().OfType<Button>()
                .Where(b => Equals(b.Content, LocalizationService.Instance.Translate("LogicPanel.ResetRegisters")))
                .ShouldAllBe(b => !b.IsEffectivelyVisible,
                    "without registers the Reset button stays hidden");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// The new Reset key exists with a non-empty value in all five shipped languages,
    /// and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewResetKeys_ExistAndAreTranslatedInAllFiveLanguages()
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

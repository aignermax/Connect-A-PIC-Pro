using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the #928 "Help (?)" flyouts (round 3 of
/// #881/#907): the Design Checks panel carries a new <see cref="HelpFlyoutButton"/>
/// explaining why the DRC-lite rules exist (bend radiation, crosstalk, reflections,
/// PDK provenance), and the connection-routing flyout gains an Auto/Bend/S-Bend
/// section. Same pattern as <c>Issue907HelpFlyoutRenderTests</c>: the render tests
/// run under German so a missing translation falls back to English and trips the
/// not-English assertion. Runs in the parallelization-free LocalizationSingleton
/// collection because the render tests mutate the process-wide language.
/// </summary>
[Collection("LocalizationSingleton")]
public class Issue928HelpFlyoutRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "DesignChecksHelp.Title",
        "DesignChecksHelp.Intro",
        "DesignChecksHelp.BendTitle",
        "DesignChecksHelp.BendBody",
        "DesignChecksHelp.SpacingTitle",
        "DesignChecksHelp.SpacingBody",
        "DesignChecksHelp.PinsTitle",
        "DesignChecksHelp.PinsBody",
        "DesignChecksHelp.SourceTitle",
        "DesignChecksHelp.SourceBody",
        "BendRadiusHelp.SbendTitle",
        "BendRadiusHelp.SbendBody",
    };

    /// <summary>The Design Checks "?" opens a flyout titled "Warum Design-Checks?".</summary>
    [AvaloniaFact]
    public void DesignChecksHelpFlyout_OpensAndShowsLocalizedTitle()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var panel = new DesignChecksPanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the Design Checks panel must carry the #928 help button");
            AssertFlyoutOpensWithTitle(window, help, "DesignChecksHelp.Title", "Why design checks?");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>The routing flyout's new S-Bend section renders its localized title.</summary>
    [AvaloniaFact]
    public async Task ConnectionRoutingHelpFlyout_ShowsSBendSection()
    {
        var previous = LocalizationService.Instance.ActiveLanguageCode;
        LocalizationService.Instance.SetLanguage(TestLanguage);
        Window? window = null;
        try
        {
            var canvas = new DesignCanvasViewModel();
            var start = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            start.PhysicalX = 40;
            start.PhysicalY = 0;
            var end = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            end.PhysicalX = 490;
            end.PhysicalY = 30;
            canvas.AddComponent(start);
            canvas.AddComponent(end);
            var connection = await canvas.ConnectPinsAsync(
                start.PhysicalPins.First(p => p.Name == "out"),
                end.PhysicalPins.First(p => p.Name == "in"));
            connection.ShouldNotBeNull();

            var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
            // The routing panel only renders while a connection is selected.
            vm.CanvasInteraction.SelectedWaveguideConnection = connection!;

            var panel = new ConnectionRoutingPanel { DataContext = vm };
            window = new Window
            {
                Width = 460,
                Height = 700,
                Content = new ScrollViewer { Content = panel },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = panel.GetVisualDescendants().OfType<HelpFlyoutButton>().FirstOrDefault();
            help.ShouldNotBeNull("the connection routing panel must carry the bend-radius help button");

            var expected = LocalizationService.Instance.Translate("BendRadiusHelp.SbendTitle");
            expected.ShouldNotBe("Auto, Bend or S-Bend?",
                "test language must translate BendRadiusHelp.SbendTitle — an English value means a missing key");

            var innerButton = help.GetVisualDescendants().OfType<Button>().First();
            innerButton.Flyout.ShouldNotBeNull("the help button must host a flyout");
            innerButton.Flyout!.ShowAt(innerButton);
            Dispatcher.UIThread.RunJobs();

            window.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == expected)
                .ShouldBeTrue($"opening the flyout must show the S-Bend section title '{expected}'");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>
    /// Every new #928 key exists with a non-empty value in all five shipped languages,
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

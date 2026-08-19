using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Views.Panels;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Headless render and resource tests for the Truth Table panel: the panel
/// renders in the right properties area with its help "(?)" flyout, shows a hint
/// unless exactly one group is selected, lists the group's external pins as
/// checkboxes, and every new string is translated in all five shipped languages.
/// Same pattern as <c>Issue928HelpFlyoutRenderTests</c>: the render tests run under
/// German so a missing translation falls back to English and trips the assertion.
/// </summary>
[Collection("LocalizationSingleton")]
public class TruthTablePanelRenderTests
{
    private const string TestLanguage = "de";

    private static readonly string[] NewKeys =
    {
        "TruthTable.Title",
        "TruthTable.NoGroupSelected",
        "TruthTable.Inputs",
        "TruthTable.Outputs",
        "TruthTable.Bias",
        "TruthTable.BiasSummary",
        "TruthTable.Threshold",
        "TruthTable.Wavelength",
        "TruthTable.Extract",
        "TruthTable.Cancel",
        "TruthTable.Signal",
        "TruthTable.SignalWatermark",
        "TruthTable.SignalWarnCrossRole",
        "TruthTable.SignalWarnDuplicateOutput",
        "TruthTableHelp.Title",
        "TruthTableHelp.Intro",
        "TruthTableHelp.ThresholdTitle",
        "TruthTableHelp.ThresholdBody",
        "TruthTableHelp.InterferenceTitle",
        "TruthTableHelp.InterferenceBody",
        "TruthTableHelp.BiasTitle",
        "TruthTableHelp.BiasBody",
        "TruthTableHelp.SignalsTitle",
        "TruthTableHelp.SignalsBody",
        "Analysis.TruthTable.Running",
        "Analysis.TruthTable.Complete",
        "Analysis.TruthTable.Cancelled",
        "Analysis.TruthTable.Failed",
        "Analysis.TruthTable.SelectPins",
        "Analysis.TruthTable.TooManyInputs",
    };

    /// <summary>The Truth Table panel's "?" opens a flyout with the localized title.</summary>
    [AvaloniaFact]
    public void TruthTableHelpFlyout_OpensAndShowsLocalizedTitle()
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

            var expected = LocalizationService.Instance.Translate("TruthTableHelp.Title");
            expected.ShouldNotBe("Why a truth table?",
                "test language must translate TruthTableHelp.Title — an English value means a missing key");

            var innerButton = help.GetVisualDescendants().OfType<Button>().First();
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

    /// <summary>Without a selected group the panel shows the hint text.</summary>
    [AvaloniaFact]
    public void TruthTablePanel_NoGroupSelected_ShowsHint()
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

            var hint = LocalizationService.Instance.Translate("TruthTable.NoGroupSelected");
            panel.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == hint)
                .ShouldBeTrue("the panel must explain that exactly one group needs to be selected");
            panel.GetVisualDescendants().OfType<CheckBox>()
                .ShouldBeEmpty("no group selected — no pin checkboxes");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(previous);
        }
    }

    /// <summary>With a group selected the panel lists its external pins as checkboxes.</summary>
    [AvaloniaFact]
    public void TruthTablePanel_GroupSelected_RendersPinCheckboxes()
    {
        Window? window = null;
        try
        {
            var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
            var groupVm = canvas.AddComponent(LogicGateFixtureFactory.CreateCombinerGroup());
            var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
            canvas.Selection.SelectSingle(groupVm);
            vm.RightPanel.TruthTable.ConfigureForSelection(groupVm, canvas);

            var panel = new TruthTablePanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var checkBoxes = panel.GetVisualDescendants().OfType<CheckBox>().ToList();
            checkBoxes.Count.ShouldBe(9, "3 external pins offered as inputs, as outputs, and as bias");
            checkBoxes.Select(c => c.Content?.ToString()).ShouldContain("a");
            checkBoxes.Select(c => c.Content?.ToString()).ShouldContain("y");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A signal name colliding across the design (issue #1071) shows the localized
    /// inline hint under the field: naming an input like another gate's output raises
    /// the cross-role warning at typing time.
    /// </summary>
    [AvaloniaFact]
    public void TruthTablePanel_CollidingSignalName_ShowsInlineWarning()
    {
        Window? window = null;
        try
        {
            var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
            var sumVm = canvas.AddComponent(CreateNotGateWithPersistedRoles("SUM"));
            var invVm = canvas.AddComponent(CreateNotGateWithPersistedRoles("INV"));
            var vm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);

            canvas.Selection.SelectSingle(sumVm);
            vm.RightPanel.TruthTable.ConfigureForSelection(sumVm, canvas);
            vm.RightPanel.TruthTable.OutputPins.Single(p => p.PinName == "Y").SignalName = "S";

            canvas.Selection.SelectSingle(invVm);
            vm.RightPanel.TruthTable.ConfigureForSelection(invVm, canvas);
            vm.RightPanel.TruthTable.InputPins.Single(p => p.PinName == "A").SignalName = "S";

            var panel = new TruthTablePanel { DataContext = vm };
            window = new Window { Width = 460, Height = 700, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var expected = string.Format(
                LocalizationService.Instance.Translate("TruthTable.SignalWarnCrossRole"), "S");
            panel.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == expected)
                .ShouldBeTrue("the colliding input row must show the cross-role hint inline");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Every new truth-table key exists with a non-empty value in all five shipped languages,
    /// and no non-English language silently falls back to the English text.
    /// </summary>
    [Fact]
    public void NewTruthTableKeys_ExistAndAreTranslatedInAllFiveLanguages()
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

    /// <summary>A bare NOT-shaped group with persisted roles but no signal names yet.</summary>
    private static ComponentGroup CreateNotGateWithPersistedRoles(string groupName)
    {
        var group = new ComponentGroup(groupName);
        foreach (var pinName in new[] { "A", "BIAS", "Y" })
        {
            var physicalPin = new PhysicalPin { Name = pinName, ParentComponent = group };
            group.PhysicalPins.Add(physicalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = physicalPin });
        }
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "A" },
            OutputPinNames = new List<string> { "Y" },
            BiasPinNames = new List<string> { "BIAS" },
            Threshold = PinnedGateTables.NotThreshold,
        };
        return group;
    }
}

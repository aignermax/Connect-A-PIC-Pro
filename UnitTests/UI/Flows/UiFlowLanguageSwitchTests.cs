using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// Field repro (round 5): with the REAL MainWindow open, switching the UI language must
/// re-translate the visible texts live — the exact same control instances, no window
/// rebuild, no app restart. Covers the AXAML <c>{loc:Localize}</c> mass (toolbar
/// "Mode:" label as witness); the VM-side one-time <c>Translate</c> strings are covered
/// by <c>MainViewModelLocalizationTests</c>.
/// Runs in the parallelization-free LocalizationSingleton collection because it mutates
/// the process-wide language.
/// </summary>
[Trait("Category", "UiFlows")]
// Boots the real MainWindow through the input pipeline — too heavy for local default
// runs (CI covers it, the local runners exclude Category=Slow).
[Trait("Category", "Slow")]
[Collection("LocalizationSingleton")]
public class UiFlowLanguageSwitchTests
{
    [AvaloniaFact]
    public void SwitchingLanguage_UpdatesOpenMainWindowLive()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        using var host = new UiFlowTestHost();
        try
        {
            // The toolbar mode label is a plain {loc:Localize Toolbar.ModeLabel} TextBlock.
            var modeLabel = UiInput.Descendants<TextBlock>(host.Window)
                .First(t => t.Text == "Mode:");

            LocalizationService.Instance.SetLanguage("es");
            UiInput.RunJobs();

            modeLabel.Text.ShouldBe("Modo:",
                "an open window's {loc:Localize} bindings must re-read live on a language switch");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
            UiInput.RunJobs();
        }
    }
}

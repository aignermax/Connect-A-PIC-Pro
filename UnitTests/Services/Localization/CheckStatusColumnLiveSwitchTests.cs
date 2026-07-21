using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Converters;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Round-5 review [6]: the Check-All batch table's Status column translated
/// imperatively at bind time, so a live language switch flipped every
/// <c>{loc:Localize}</c> label but left the status texts in the previous language
/// until the next Check-All run. The column now uses a MultiBinding whose second
/// input is the <see cref="LocalizationService"/> indexer — the same "Item"
/// notification that drives <see cref="LocalizeExtension"/> re-fires the row.
/// </summary>
[Collection("LocalizationSingleton")]
public class CheckStatusColumnLiveSwitchTests
{
    [AvaloniaFact]
    public void SetLanguage_WhileBatchRowIsShowing_RetranslatesTheStatusColumn()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        Window? window = null;
        try
        {
            var row = new ComponentCheckResult(
                "ebeam_dc_halfring", ComponentCheckStatus.Misaligned, 4, 4, 9.96, "off");

            // Mirrors the PdkOffsetEditorWindow.axaml Status-column binding.
            var textBlock = new TextBlock { DataContext = row };
            var multi = new MultiBinding
            {
                Converter = ComponentCheckStatusToTextConverter.Instance,
                Bindings =
                {
                    new Binding(nameof(ComponentCheckResult.Status)),
                    new Binding("[PdkOffset.Status]") { Source = LocalizationService.Instance },
                },
            };
            textBlock.Bind(TextBlock.TextProperty, multi);

            window = new Window { Content = textBlock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            textBlock.Text.ShouldBe("Misaligned", "sanity: English status text initially");

            LocalizationService.Instance.SetLanguage("es");
            Dispatcher.UIThread.RunJobs();

            textBlock.Text.ShouldBe("Desalineado",
                "the batch report's status column must re-translate live on SetLanguage");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }

    [Fact]
    public void MultiConvert_TranslatesTheFirstValue_IgnoringTheTrigger()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

        var result = ComponentCheckStatusToTextConverter.Instance.Convert(
            new object?[] { ComponentCheckStatus.Aligned, "ignored trigger" },
            typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        result.ShouldBe("Aligned");
    }
}

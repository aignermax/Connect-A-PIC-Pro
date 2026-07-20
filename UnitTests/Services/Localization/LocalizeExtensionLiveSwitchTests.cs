using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Guards the live language switch at the binding level (field bug, round 5): a control
/// bound via <see cref="LocalizeExtension"/> must re-read its text when
/// <see cref="LocalizationService.SetLanguage"/> is called while the control is showing.
/// Avalonia's <c>ReflectionIndexerNode.ShouldUpdate</c> only re-reads a CLR <c>[Key]</c>
/// indexer binding when the <c>PropertyChanged</c> name resolves to the indexer property
/// itself ("Item") — neither "Item[]" (WPF convention) nor the empty "all properties"
/// name qualify, so raising the wrong name leaves every open window in the old language.
/// </summary>
[Collection("LocalizationSingleton")]
public class LocalizeExtensionLiveSwitchTests
{
    [AvaloniaFact]
    public void SetLanguage_WhileControlIsShowing_RefreshesLocalizeBindingLive()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        Window? window = null;
        try
        {
            var textBlock = new TextBlock();
            var binding = (Binding)new LocalizeExtension("Common.Ok").ProvideValue(null!);
            textBlock.Bind(TextBlock.TextProperty, binding);

            window = new Window { Content = textBlock };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            textBlock.Text.ShouldBe("OK", "the binding must resolve the English text initially");

            LocalizationService.Instance.SetLanguage("es");
            Dispatcher.UIThread.RunJobs();

            textBlock.Text.ShouldBe("Aceptar",
                "a {loc:Localize} binding must re-read live on SetLanguage — without recreating the control");
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }
}

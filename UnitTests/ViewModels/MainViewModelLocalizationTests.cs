using System.Globalization;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Guards the VM-side live language switch (field bug, round 5): status-bar texts the
/// MainViewModel translated once via <c>Translate(...)</c> — the idle "Ready" text and
/// the startup "Loaded N component types" message — must re-translate when the UI
/// language changes while they are showing. Plain transient messages (e.g. migration
/// warnings) intentionally stay untouched. Mutates the process-wide
/// <see cref="LocalizationService.Instance"/>, hence the parallelization-free collection.
/// </summary>
[Collection("LocalizationSingleton")]
public class MainViewModelLocalizationTests
{
    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, LocalizationService.Instance.Translate(key), args);

    [Fact]
    public void SetLanguage_AfterStartup_ReTranslatesTheStartupStatus()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            var count = vm.LeftPanel.AllTemplates.Count;
            vm.StatusText.ShouldBe(Format("Status.LoadedComponentTypes", count),
                "precondition: after startup the status bar shows the loaded-templates message");

            LocalizationService.Instance.SetLanguage("es");

            vm.StatusText.ShouldBe(Format("Status.LoadedComponentTypes", count),
                "the startup status must re-translate on a live language switch");
            vm.StatusText.ShouldContain("componentes");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }

    [Fact]
    public void SetLanguage_WhileIdleStatusIsShowing_ReTranslatesStatusBar()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            vm.SetLocalizedStatus("Status.Ready");
            vm.StatusText.ShouldBe("Ready");

            LocalizationService.Instance.SetLanguage("es");

            vm.StatusText.ShouldBe("Listo",
                "the idle status-bar text must re-translate on a live language switch");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }

    [Fact]
    public void SetLanguage_WhileTransientStatusIsShowing_LeavesItUntouched()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            vm.StatusText = "Migration warning: XY";

            LocalizationService.Instance.SetLanguage("es");

            vm.StatusText.ShouldBe("Migration warning: XY",
                "meaningful transient messages (e.g. migration warnings) must survive a language switch");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }

    [Fact]
    public void SetLanguage_ChainedSwitches_KeepReTranslatingTheIdleStatus()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        try
        {
            var vm = MainViewModelTestHelper.CreateMainViewModel();
            vm.SetLocalizedStatus("Status.Ready");

            LocalizationService.Instance.SetLanguage("de");
            vm.StatusText.ShouldBe("Bereit");

            LocalizationService.Instance.SetLanguage("es");
            vm.StatusText.ShouldBe("Listo",
                "a second switch must still recognize the (re-translated) status");
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        }
    }
}

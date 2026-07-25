using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Solvers;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

public class Tidy3dSettingsViewModelTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    public Tidy3dSettingsViewModelTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    public void Dispose()
    {
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public void ApiKey_PersistsImmediatelyOnChange()
    {
        var prefs = new UserPreferencesService(_prefsPath);
        var vm = new Tidy3dSettingsViewModel(prefs);
        vm.IsApiKeySet.ShouldBeFalse();

        vm.ApiKey = "tidy3d-key-123";

        vm.IsApiKeySet.ShouldBeTrue();
        vm.StatusText.ShouldContain("saved");
        new UserPreferencesService(_prefsPath).GetTidy3dApiKey().ShouldBe("tidy3d-key-123");
    }

    [Fact]
    public void ClearingApiKey_PersistsAndWarns()
    {
        var prefs = new UserPreferencesService(_prefsPath);
        prefs.SetTidy3dApiKey("old-key");
        var vm = new Tidy3dSettingsViewModel(prefs);
        vm.ApiKey.ShouldBe("old-key");

        vm.ApiKey = "";

        vm.IsApiKeySet.ShouldBeFalse();
        vm.StatusText.ShouldContain("cleared");
        new UserPreferencesService(_prefsPath).GetTidy3dApiKey().ShouldBeEmpty();
    }
}

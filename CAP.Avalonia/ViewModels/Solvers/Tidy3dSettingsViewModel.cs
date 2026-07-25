using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers;

// Settings-page ViewModel for the Tidy3D cloud solver API key, stored in user
// preferences and passed to the tidy3d Python package via SIMCLOUD_APIKEY.
public partial class Tidy3dSettingsViewModel : ObservableObject
{
    private readonly UserPreferencesService _preferences;
    private readonly IUrlLauncher? _urlLauncher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeySet))]
    private string _apiKey;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool IsApiKeySet => !string.IsNullOrWhiteSpace(ApiKey);

    public Tidy3dSettingsViewModel(UserPreferencesService preferences, IUrlLauncher? urlLauncher = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _urlLauncher = urlLauncher;
        _apiKey = preferences.GetTidy3dApiKey();
    }

    partial void OnApiKeyChanged(string value)
    {
        _preferences.SetTidy3dApiKey(value);
        StatusText = LocalizationService.Instance.Translate(
            string.IsNullOrWhiteSpace(value) ? "Settings.Tidy3d.KeyCleared" : "Settings.Tidy3d.KeySaved");
    }

    /// <summary>Opens the Tidy3D web portal (account / API-key management) in the browser.</summary>
    [RelayCommand]
    private void OpenPortal() => _urlLauncher?.Open(FdtdBackendSelectionViewModel.Tidy3dPortalUrl);
}

using CAP.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Solvers;

/// <summary>
/// Settings-page ViewModel for the Tidy3D cloud solver credentials. The API key
/// is stored once in user preferences and shared by every Tidy3D consumer
/// (FDTD S-matrix backend and the Tidy3D mode-solver backend).
/// </summary>
public partial class Tidy3dSettingsViewModel : ObservableObject
{
    private readonly UserPreferencesService _preferences;

    /// <summary>Tidy3D API key; persisted on every change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeySet))]
    private string _apiKey;

    /// <summary>Feedback line shown under the key field after a change.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>True when a key is configured (drives the status hint).</summary>
    public bool IsApiKeySet => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Loads the persisted key.</summary>
    public Tidy3dSettingsViewModel(UserPreferencesService preferences)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _apiKey = preferences.GetTidy3dApiKey();
    }

    partial void OnApiKeyChanged(string value)
    {
        _preferences.SetTidy3dApiKey(value);
        StatusText = string.IsNullOrWhiteSpace(value)
            ? "API key cleared — the Tidy3D backend is unavailable until a key is set."
            : "API key saved. It is stored locally and passed to tidy3d via SIMCLOUD_APIKEY.";
    }
}

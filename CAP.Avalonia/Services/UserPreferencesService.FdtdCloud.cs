using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services;

/// <summary>
/// FDTD cloud/backend preference access for <see cref="UserPreferencesService"/> —
/// the Tidy3D API key and the persisted FDTD backend choice. Split into its own
/// partial file to keep the main file under the 500-line limit.
/// </summary>
public partial class UserPreferencesService
{
    /// <summary>
    /// Gets the API key for the Tidy3D cloud solver. Empty string when not configured.
    /// </summary>
    public string GetTidy3dApiKey()
    {
        return _preferences.Tidy3dApiKey;
    }

    /// <summary>
    /// Sets the Tidy3D API key and saves preferences.
    /// </summary>
    public void SetTidy3dApiKey(string apiKey)
    {
        _preferences.Tidy3dApiKey = apiKey ?? "";
        Save();
    }

    /// <summary>
    /// Gets the persisted FDTD S-matrix backend choice (defaults to local Meep/Docker
    /// when unset or unparseable, so existing installs keep their behaviour).
    /// </summary>
    public FdtdBackendType GetFdtdBackend()
    {
        return Enum.TryParse<FdtdBackendType>(_preferences.FdtdBackend, out var backend)
            ? backend
            : FdtdBackendType.MeepDocker;
    }

    /// <summary>
    /// Persists the FDTD S-matrix backend choice.
    /// </summary>
    public void SetFdtdBackend(FdtdBackendType backend)
    {
        _preferences.FdtdBackend = backend.ToString();
        Save();
    }
}

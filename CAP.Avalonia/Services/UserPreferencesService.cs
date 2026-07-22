using System.Text.Json;
using CAP_Core;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Update;

namespace CAP.Avalonia.Services;

/// <summary>
/// Manages user preferences with JSON file persistence.
/// Stores PDK filter states and other user settings.
/// </summary>
public class UserPreferencesService
{
    private readonly string _preferencesFilePath;
    private readonly ErrorConsoleService? _errorConsole;
    private UserPreferences _preferences;

    public UserPreferencesService(ErrorConsoleService? errorConsole = null)
    {
        var appDataDir = GetAppDataDirectory();
        Directory.CreateDirectory(appDataDir);
        _preferencesFilePath = Path.Combine(appDataDir, "user-preferences.json");
        _errorConsole = errorConsole;
        _preferences = LoadPreferences();
    }

    /// <summary>
    /// Test constructor that uses a custom file path.
    /// Used by unit tests to avoid polluting user preferences.
    /// </summary>
    internal UserPreferencesService(string testFilePath, ErrorConsoleService? errorConsole = null)
    {
        _preferencesFilePath = testFilePath;
        _errorConsole = errorConsole;
        _preferences = LoadPreferences();
    }

    /// <summary>
    /// Gets the app data directory for Lunima.
    /// </summary>
    private static string GetAppDataDirectory()
    {
        var baseDir = CAP_Core.Helpers.AppDataFolders.LocalApplicationData;
        return Path.Combine(baseDir, "Lunima");
    }

    /// <summary>
    /// Loads preferences from disk or creates defaults. On corruption the bad file is renamed
    /// to {filename}.bak so the next Save() doesn't overwrite the user's data, and the failure
    /// is logged to the error console (if available).
    /// </summary>
    private UserPreferences LoadPreferences()
    {
        if (!File.Exists(_preferencesFilePath))
            return new UserPreferences();

        try
        {
            var json = File.ReadAllText(_preferencesFilePath);
            var prefs = JsonSerializer.Deserialize<UserPreferences>(json);
            return prefs ?? new UserPreferences();
        }
        catch (JsonException ex)
        {
            BackupAndLog(ex, "Preferences file is corrupted");
        }
        catch (IOException ex)
        {
            _errorConsole?.LogError($"Could not read preferences from '{_preferencesFilePath}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _errorConsole?.LogError($"Access denied reading preferences from '{_preferencesFilePath}': {ex.Message}", ex);
        }

        return new UserPreferences();
    }

    private void BackupAndLog(Exception ex, string reason)
    {
        var backupPath = _preferencesFilePath + ".bak";
        try
        {
            File.Move(_preferencesFilePath, backupPath, overwrite: true);
            _errorConsole?.LogError(
                $"{reason}. Original kept at '{backupPath}'; defaults will be used. ({ex.Message})", ex);
        }
        catch (IOException backupEx)
        {
            _errorConsole?.LogError(
                $"{reason} and backup also failed ({backupEx.Message}). Preferences will reset to defaults.", ex);
        }
        catch (UnauthorizedAccessException backupEx)
        {
            _errorConsole?.LogError(
                $"{reason} and backup also failed ({backupEx.Message}). Preferences will reset to defaults.", ex);
        }
    }

    /// <summary>
    /// Saves current preferences to disk. Returns true on success; logs and returns false otherwise.
    /// </summary>
    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_preferences, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_preferencesFilePath, json);
            return true;
        }
        catch (IOException ex)
        {
            _errorConsole?.LogWarning($"Could not save preferences to '{_preferencesFilePath}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _errorConsole?.LogWarning($"Access denied saving preferences to '{_preferencesFilePath}': {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Gets the list of enabled PDK names.
    /// </summary>
    public HashSet<string> GetEnabledPdks()
    {
        return new HashSet<string>(_preferences.EnabledPdks, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets the list of enabled PDK names and saves.
    /// </summary>
    public void SetEnabledPdks(IEnumerable<string> pdkNames)
    {
        _preferences.EnabledPdks = pdkNames.ToList();
        Save();
    }

    /// <summary>
    /// Every PDK name the last filter-state save saw as loaded — enabled or not. Distinguishes a
    /// PDK the user deliberately unchecked (known, absent from <see cref="GetEnabledPdks"/>) from
    /// one the save never saw (e.g. created under a process lock, where the filter state is not
    /// persisted): only the former must restore as disabled (PR #739 review). Empty for
    /// preferences written before this list existed.
    /// </summary>
    public HashSet<string> GetKnownPdks()
    {
        return new HashSet<string>(_preferences.KnownPdks, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets both PDK filter lists — the enabled names and the full set of loaded ("known")
    /// names — in one save. See <see cref="GetKnownPdks"/> for why the second list exists.
    /// </summary>
    public void SetPdkFilterState(IEnumerable<string> enabledPdkNames, IEnumerable<string> knownPdkNames)
    {
        _preferences.EnabledPdks = enabledPdkNames.ToList();
        _preferences.KnownPdks = knownPdkNames.ToList();
        Save();
    }

    /// <summary>
    /// Gets the list of user-loaded PDK file paths for auto-reload.
    /// </summary>
    public List<string> GetUserPdkPaths()
    {
        return new List<string>(_preferences.UserPdkPaths);
    }

    /// <summary>
    /// Adds a user-loaded PDK path and saves.
    /// </summary>
    public void AddUserPdkPath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (!_preferences.UserPdkPaths.Contains(normalizedPath))
        {
            _preferences.UserPdkPaths.Add(normalizedPath);
            Save();
        }
    }

    /// <summary>
    /// Removes a user-loaded PDK path and saves.
    /// </summary>
    public void RemoveUserPdkPath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_preferences.UserPdkPaths.Remove(normalizedPath))
        {
            Save();
        }
    }

    /// <summary>
    /// Gets the saved left panel width (default 220 pixels).
    /// </summary>
    public double GetLeftPanelWidth()
    {
        return _preferences.LeftPanelWidth;
    }

    /// <summary>
    /// Sets the left panel width and saves.
    /// </summary>
    public void SetLeftPanelWidth(double width)
    {
        _preferences.LeftPanelWidth = width;
        Save();
    }

    /// <summary>
    /// Gets the saved right panel width (default 250 pixels).
    /// </summary>
    public double GetRightPanelWidth()
    {
        return _preferences.RightPanelWidth;
    }

    /// <summary>
    /// Sets the right panel width and saves.
    /// </summary>
    public void SetRightPanelWidth(double width)
    {
        _preferences.RightPanelWidth = width;
        Save();
    }

    /// <summary>
    /// Gets the custom Python path, or null if using system default.
    /// </summary>
    public string? GetCustomPythonPath()
    {
        return _preferences.CustomPythonPath;
    }

    /// <summary>
    /// Sets the custom Python path and saves.
    /// </summary>
    public void SetCustomPythonPath(string? pythonPath)
    {
        _preferences.CustomPythonPath = pythonPath;
        Save();
    }

    /// <summary>
    /// Gets the version the user chose to skip during update prompts.
    /// Returns null if no version was skipped.
    /// </summary>
    public SemanticVersion? GetSkippedUpdateVersion()
    {
        return SemanticVersion.TryParse(_preferences.SkippedUpdateVersion, out var v) ? v : null;
    }

    /// <summary>
    /// Stores the version the user chose to skip and saves preferences.
    /// </summary>
    public void SetSkippedUpdateVersion(SemanticVersion version)
    {
        _preferences.SkippedUpdateVersion = version.ToString();
        Save();
    }

    /// <summary>
    /// Gets the API key for the AI Design Assistant.
    /// Returns an empty string if not configured.
    /// </summary>
    public string GetAiApiKey()
    {
        return _preferences.AiApiKey;
    }

    /// <summary>
    /// Sets the API key for the AI Design Assistant and saves preferences.
    /// </summary>
    public void SetAiApiKey(string apiKey)
    {
        _preferences.AiApiKey = apiKey ?? "";
        Save();
    }

    /// <summary>
    /// Gets the default chip width in millimeters used for new projects.
    /// Falls back to 5 mm if not configured.
    /// </summary>
    public double GetDefaultChipWidthMm() => _preferences.DefaultChipWidthMm;

    /// <summary>
    /// Gets the default chip height in millimeters used for new projects.
    /// Falls back to 5 mm if not configured.
    /// </summary>
    public double GetDefaultChipHeightMm() => _preferences.DefaultChipHeightMm;

    /// <summary>
    /// Sets the default chip dimensions (in millimeters) used for new projects and saves.
    /// </summary>
    public void SetDefaultChipSize(double widthMm, double heightMm)
    {
        _preferences.DefaultChipWidthMm  = widthMm;
        _preferences.DefaultChipHeightMm = heightMm;
        Save();
    }

    /// <summary>
    /// Gets the global interconnect settings (waveguide width/bend radius/GDS layer).
    /// Falls back to the historical export defaults when not configured.
    /// </summary>
    public InterconnectSettings GetInterconnectSettings() => new()
    {
        WidthMicrometers = _preferences.InterconnectWidthMicrometers
            ?? InterconnectSettings.DefaultWidthMicrometers,
        BendRadiusMicrometers = _preferences.InterconnectBendRadiusMicrometers
            ?? InterconnectSettings.DefaultBendRadiusMicrometers,
        GdsLayer = _preferences.InterconnectGdsLayer,
    };

    /// <summary>
    /// Sets the global interconnect settings and saves preferences.
    /// </summary>
    public void SetInterconnectSettings(InterconnectSettings settings)
    {
        _preferences.InterconnectWidthMicrometers = settings.WidthMicrometers;
        _preferences.InterconnectBendRadiusMicrometers = settings.BendRadiusMicrometers;
        _preferences.InterconnectGdsLayer = settings.GdsLayer;
        Save();
    }

    /// <summary>
    /// Gets the UI language preference: a shipped language code ("en", "de",
    /// "zh-Hans", "es") or "system" to follow the OS display language (default).
    /// </summary>
    public string GetUiLanguage() => _preferences.UiLanguage;

    /// <summary>
    /// Sets the UI language preference and saves. See <see cref="GetUiLanguage"/> for values.
    /// </summary>
    public void SetUiLanguage(string languageCodeOrSystem)
    {
        _preferences.UiLanguage = languageCodeOrSystem;
        Save();
    }

    /// <summary>
    /// Clears any skipped update version and saves preferences.
    /// </summary>
    public void ClearSkippedUpdateVersion()
    {
        _preferences.SkippedUpdateVersion = null;
        Save();
    }

    /// <summary>
    /// Records that the user chose "Skip for Today", suppressing the startup notification
    /// until the next calendar day.
    /// </summary>
    public void SkipToday()
    {
        _preferences.SkipTodayDate = DateTime.UtcNow.Date;
        Save();
    }

    /// <summary>
    /// Returns <c>true</c> when the startup update check should run today.
    /// Returns <c>false</c> if the user already clicked "Skip for Today" today.
    /// </summary>
    public bool ShouldCheckToday()
    {
        return _preferences.SkipTodayDate == null
            || _preferences.SkipTodayDate.Value.Date < DateTime.UtcNow.Date;
    }
}

/// <summary>
/// User preferences data structure.
/// </summary>
public class UserPreferences
{
    /// <summary>
    /// List of PDK names that are currently enabled (visible in library).
    /// </summary>
    public List<string> EnabledPdks { get; set; } = new();

    /// <summary>
    /// All PDK names loaded at the last filter-state save (enabled or not) — lets restore tell a
    /// deliberately-unchecked PDK from one it has never seen. See
    /// <see cref="UserPreferencesService.GetKnownPdks"/>.
    /// </summary>
    public List<string> KnownPdks { get; set; } = new();

    /// <summary>
    /// List of user-loaded PDK file paths to auto-load at startup.
    /// </summary>
    public List<string> UserPdkPaths { get; set; } = new();

    /// <summary>
    /// Width of the left panel in pixels (default 220).
    /// </summary>
    public double LeftPanelWidth { get; set; } = 220;

    /// <summary>
    /// Width of the right panel in pixels (default 250).
    /// </summary>
    public double RightPanelWidth { get; set; } = 250;

    /// <summary>
    /// Custom Python executable path for Nazca/GDS export.
    /// If null, system default (python3/python) will be used.
    /// </summary>
    public string? CustomPythonPath { get; set; }

    /// <summary>
    /// Version string the user chose to skip during update prompts (e.g. "1.2.3").
    /// Null means no version is skipped.
    /// </summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>
    /// UTC date when the user last chose "Skip for Today".
    /// Null means never skipped. Reset daily.
    /// </summary>
    public DateTime? SkipTodayDate { get; set; }

    /// <summary>
    /// Encrypted API key for the AI Design Assistant (Claude/Anthropic).
    /// Empty string means no key is configured.
    /// </summary>
    public string AiApiKey { get; set; } = "";

    /// <summary>
    /// Default chip width in millimeters for new projects (default 5 mm).
    /// </summary>
    public double DefaultChipWidthMm { get; set; } = 5.0;

    /// <summary>
    /// Default chip height in millimeters for new projects (default 5 mm).
    /// </summary>
    public double DefaultChipHeightMm { get; set; } = 5.0;

    /// <summary>
    /// Global interconnect waveguide width in µm. Null = export default (0.45).
    /// </summary>
    public double? InterconnectWidthMicrometers { get; set; }

    /// <summary>
    /// Global interconnect bend radius in µm. Null = export default (50).
    /// </summary>
    public double? InterconnectBendRadiusMicrometers { get; set; }

    /// <summary>
    /// Global interconnect GDS layer. Null = PDK/Nazca default layer.
    /// </summary>
    public int? InterconnectGdsLayer { get; set; }

    /// <summary>
    /// UI language: a shipped language code ("en", "de", "zh-Hans", "es") or
    /// "system" (default) to auto-detect the OS display language at startup.
    /// </summary>
    public string UiLanguage { get; set; } = "system";
}

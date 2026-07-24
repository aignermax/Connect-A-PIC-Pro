namespace CAP.Avalonia.Services;

/// <summary>
/// Recent-projects preference access for <see cref="UserPreferencesService"/> — the
/// Home screen's recent-projects list and reopen-last-project-on-startup setting.
/// Split into its own partial file to keep the main file under the 500-line limit.
/// </summary>
public partial class UserPreferencesService
{
    /// <summary>
    /// Gets a copy of the recent-projects list (most recently opened first).
    /// Use <see cref="RecentProjectsService"/> for MRU ordering/dedup logic.
    /// </summary>
    public List<RecentProjectEntry> GetRecentProjects()
    {
        return new List<RecentProjectEntry>(_preferences.RecentProjects);
    }

    /// <summary>
    /// Replaces the recent-projects list and saves.
    /// </summary>
    public void SetRecentProjects(IEnumerable<RecentProjectEntry> entries)
    {
        _preferences.RecentProjects = entries.ToList();
        Save();
    }

    /// <summary>
    /// Gets whether the app should reopen the most recent project at startup
    /// instead of showing the Home screen.
    /// </summary>
    public bool GetReopenLastProjectOnStartup()
    {
        return _preferences.ReopenLastProjectOnStartup;
    }

    /// <summary>
    /// Sets the reopen-last-project-on-startup preference and saves.
    /// </summary>
    public void SetReopenLastProjectOnStartup(bool enabled)
    {
        _preferences.ReopenLastProjectOnStartup = enabled;
        Save();
    }
}

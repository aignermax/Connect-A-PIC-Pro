namespace CAP.Avalonia.Services;

/// <summary>
/// A single entry in the recent-projects list shown on the Home screen.
/// </summary>
public class RecentProjectEntry
{
    /// <summary>Absolute path to the .lun design file.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>UTC timestamp of when the project was last opened or saved.</summary>
    public DateTime LastOpenedUtc { get; set; }

    /// <summary>
    /// True when the user pinned this project. Pinned entries survive cap
    /// eviction and are shown first on the Home screen.
    /// </summary>
    public bool Pinned { get; set; }
}

/// <summary>
/// Maintains the most-recently-used project list (MRU) backing the Home screen.
/// Handles ordering, deduplication, the entry cap, and path normalization;
/// persistence is delegated to <see cref="UserPreferencesService"/>.
/// </summary>
public class RecentProjectsService
{
    /// <summary>Maximum number of entries kept in the recent-projects list.</summary>
    public const int MaxRecentProjects = 10;

    private readonly UserPreferencesService _preferences;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Initializes a new instance of <see cref="RecentProjectsService"/>.</summary>
    /// <param name="preferences">Preference store the list is persisted through.</param>
    /// <param name="utcNow">Clock override for deterministic tests; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public RecentProjectsService(UserPreferencesService preferences, Func<DateTime>? utcNow = null)
    {
        _preferences = preferences;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns the recent projects, most recently opened first.
    /// </summary>
    public IReadOnlyList<RecentProjectEntry> GetRecentProjects()
    {
        return _preferences.GetRecentProjects();
    }

    /// <summary>
    /// Records a project as the most recently used. The path is normalized to an
    /// absolute path; an existing entry for the same file (compared case-insensitively)
    /// moves to the front instead of duplicating. The list is capped at
    /// <see cref="MaxRecentProjects"/> entries.
    /// </summary>
    public void RecordProject(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var entries = _preferences.GetRecentProjects();

        var wasPinned = entries.FirstOrDefault(e => PathsEqual(e.FilePath, normalizedPath))?.Pinned ?? false;
        entries.RemoveAll(e => PathsEqual(e.FilePath, normalizedPath));
        entries.Insert(0, new RecentProjectEntry
        {
            FilePath = normalizedPath,
            LastOpenedUtc = _utcNow(),
            Pinned = wasPinned
        });

        EvictOverflow(entries);

        _preferences.SetRecentProjects(entries);
    }

    /// <summary>
    /// Toggles the pinned flag on the entry for the given file, if present, and saves.
    /// </summary>
    public void TogglePin(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var entries = _preferences.GetRecentProjects();

        var entry = entries.FirstOrDefault(e => PathsEqual(e.FilePath, normalizedPath));
        if (entry == null)
            return;

        entry.Pinned = !entry.Pinned;
        _preferences.SetRecentProjects(entries);
    }

    /// <summary>
    /// Removes the oldest UNPINNED entries until the list is within
    /// <see cref="MaxRecentProjects"/>. Index 0 is never evicted — it is the
    /// entry <see cref="RecordProject"/> just inserted, and dropping the
    /// project the user just opened would silently break recents and
    /// reopen-last-project. Pinned entries are never evicted either, so a
    /// fully pinned list may exceed the cap.
    /// </summary>
    private static void EvictOverflow(List<RecentProjectEntry> entries)
    {
        for (int i = entries.Count - 1; i >= 1 && entries.Count > MaxRecentProjects; i--)
        {
            if (!entries[i].Pinned)
            {
                entries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Removes the entry for the given file from the recent-projects list, if present.
    /// </summary>
    public void RemoveProject(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var entries = _preferences.GetRecentProjects();

        if (entries.RemoveAll(e => PathsEqual(e.FilePath, normalizedPath)) > 0)
        {
            _preferences.SetRecentProjects(entries);
        }
    }

    /// <summary>
    /// Removes all entries from the recent-projects list.
    /// </summary>
    public void ClearAll()
    {
        _preferences.SetRecentProjects(new List<RecentProjectEntry>());
    }

    /// <summary>
    /// Path comparison matching the platform's filesystem semantics: ordinal on
    /// Linux (case-sensitive — two paths differing only by case are distinct
    /// files), case-insensitive on Windows and (default) macOS.
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Compares two normalized paths using <see cref="PathComparison"/>.
    /// </summary>
    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, PathComparison);
    }
}

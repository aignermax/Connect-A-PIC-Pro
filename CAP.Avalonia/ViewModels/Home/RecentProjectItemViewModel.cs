using CAP.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Home;

/// <summary>
/// A single row in the Home screen's recent-projects list: display name,
/// full path, last-opened timestamp, and whether the file is missing on disk
/// (moved or deleted entries are grayed out instead of erroring on click).
/// </summary>
public partial class RecentProjectItemViewModel : ObservableObject
{
    /// <summary>Absolute path to the .lun design file.</summary>
    public string FullPath { get; }

    /// <summary>Project display name (file name without the .lun extension).</summary>
    public string FileName { get; }

    /// <summary>Last-opened timestamp formatted in local time for display.</summary>
    public string LastOpenedDisplay { get; }

    /// <summary>True when the user pinned this project (shown first, survives eviction).</summary>
    public bool IsPinned { get; }

    /// <summary>
    /// True when the file no longer exists on disk. Set at list build time and
    /// re-checked on open, so the row can be grayed out with a remove hint.
    /// </summary>
    [ObservableProperty]
    private bool _isMissing;

    /// <summary>Initializes the row from a persisted recent-projects entry.</summary>
    public RecentProjectItemViewModel(RecentProjectEntry entry)
    {
        FullPath = entry.FilePath;
        FileName = Path.GetFileNameWithoutExtension(entry.FilePath);
        LastOpenedDisplay = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        IsPinned = entry.Pinned;
        IsMissing = !File.Exists(entry.FilePath);
    }
}

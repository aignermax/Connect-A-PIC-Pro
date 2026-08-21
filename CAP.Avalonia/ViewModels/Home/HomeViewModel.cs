using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Home;

/// <summary>
/// ViewModel for the Home screen shown as the main window's startup state:
/// a recent-projects list plus New / Open / Continue actions. Project I/O is
/// delegated to <see cref="Panels.FileOperationsViewModel"/> via the
/// <c>*Requested</c> callbacks, wired by <see cref="MainViewModel"/>.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly RecentProjectsService _recentProjectsService;
    private readonly UserPreferencesService _preferences;
    private readonly ExampleDesignsService _exampleDesigns;

    /// <summary>
    /// Whether the Home screen overlay is currently shown. Starts true so the
    /// app opens on the Home screen; cleared when a project is opened/created
    /// or the user continues without one.
    /// </summary>
    [ObservableProperty]
    private bool _isHomeVisible = true;

    /// <summary>
    /// When true, the app reopens the most recent project at startup instead
    /// of waiting on the Home screen. Persisted; bound to the card's checkbox.
    /// </summary>
    [ObservableProperty]
    private bool _reopenLastProjectOnStartup;

    /// <summary>
    /// Filter applied to the recent-projects list; matches anywhere in the
    /// file path, case-insensitively.
    /// </summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>
    /// True when any recent projects exist at all, ignoring the search filter.
    /// Keeps the search box and Clear-all visible while a filter matches nothing.
    /// </summary>
    [ObservableProperty]
    private bool _hasAnyRecentProjects;

    /// <summary>
    /// True while the startup project (CLI argument or reopen-last preference)
    /// is loading. The card disables itself so a user click can't interleave
    /// with the in-flight load. Set by the main window's Loaded handler.
    /// </summary>
    [ObservableProperty]
    private bool _isStartupLoadInProgress;

    /// <summary>Recent projects, most recently opened first.</summary>
    public ObservableCollection<RecentProjectItemViewModel> RecentProjects { get; } = new();

    /// <summary>Shipped example designs; empty when none are installed.</summary>
    public ObservableCollection<ExampleDesign> Examples { get; } = new();

    /// <summary>True when any shipped examples were found (shows the Examples section).</summary>
    [ObservableProperty]
    private bool _hasExamples;

    /// <summary>Callback to create a new empty project (File → New).</summary>
    public Func<Task>? NewProjectRequested { get; set; }

    /// <summary>Callback to open a project via the file picker (File → Open).</summary>
    public Func<Task>? OpenProjectRequested { get; set; }

    /// <summary>
    /// Callback to open a project directly from a path (recent-projects click).
    /// Returns true when the project was loaded.
    /// </summary>
    public Func<string, Task<bool>>? OpenProjectFromPathRequested { get; set; }

    /// <summary>
    /// Callback to open an example design as an untitled copy (detached from
    /// the example file). Returns true when the design was opened.
    /// </summary>
    public Func<string, Task<bool>>? OpenExampleRequested { get; set; }

    /// <summary>
    /// Callback to start the first-steps guided tour on a fresh design
    /// (issue #1080). Wired by <see cref="MainViewModel"/>; creates the new
    /// project and activates the tour only when that succeeds.
    /// </summary>
    public Func<Task>? LearnTutorialRequested { get; set; }

    /// <summary>
    /// Callback to start the "Watch it compute" guided tour (issue #1143):
    /// opens the shipped Counter example as an untitled copy and activates the
    /// tour only when that succeeds.
    /// </summary>
    public Func<Task>? WatchComputeTourRequested { get; set; }

    /// <summary>Initializes the Home screen and builds the recent-projects and examples lists.</summary>
    public HomeViewModel(
        RecentProjectsService recentProjectsService,
        UserPreferencesService preferences,
        ExampleDesignsService? exampleDesigns = null)
    {
        _recentProjectsService = recentProjectsService;
        _preferences = preferences;
        _exampleDesigns = exampleDesigns ?? new ExampleDesignsService();
        _reopenLastProjectOnStartup = preferences.GetReopenLastProjectOnStartup();
        RefreshRecentProjects();
        RefreshExamples();
    }

    /// <summary>Rebuilds the shipped-examples list from disk.</summary>
    private void RefreshExamples()
    {
        Examples.Clear();
        foreach (var example in _exampleDesigns.GetExamples())
        {
            Examples.Add(example);
        }
        HasExamples = Examples.Count > 0;
    }

    /// <summary>Persists the reopen-on-startup preference when the checkbox changes.</summary>
    partial void OnReopenLastProjectOnStartupChanged(bool value)
    {
        _preferences.SetReopenLastProjectOnStartup(value);
    }

    /// <summary>Re-filters the recent list as the user types.</summary>
    partial void OnSearchTextChanged(string value)
    {
        RefreshRecentProjects();
    }

    /// <summary>
    /// Reopens the most recently used project at startup when the preference is
    /// enabled and the file still exists. Called once from the main window's
    /// Loaded handler. Returns true when a project was opened.
    /// </summary>
    public async Task<bool> TryReopenLastProjectAsync()
    {
        if (!ReopenLastProjectOnStartup || OpenProjectFromPathRequested == null)
            return false;

        var lastProject = _recentProjectsService.GetRecentProjects().FirstOrDefault();
        if (lastProject == null || !File.Exists(lastProject.FilePath))
            return false;

        return await OpenProjectFromPathRequested(lastProject.FilePath);
    }

    /// <summary>
    /// Shows the Home screen (toolbar Home button), refreshing the recent list
    /// and the examples list first — the latter so descriptions follow a
    /// language switched in Settings since startup.
    /// </summary>
    public void Show()
    {
        RefreshRecentProjects();
        RefreshExamples();
        IsHomeVisible = true;
    }

    /// <summary>
    /// Hides the Home screen after a project was opened or created. Wired to
    /// <see cref="Panels.FileOperationsViewModel.ProjectOpened"/>.
    /// </summary>
    public void OnProjectOpened()
    {
        IsHomeVisible = false;
    }

    /// <summary>
    /// Rebuilds the recent-projects rows from the persisted list: applies the
    /// search filter, orders pinned entries first (MRU order within each group),
    /// and re-checks which files still exist on disk.
    /// </summary>
    public void RefreshRecentProjects()
    {
        var entries = _recentProjectsService.GetRecentProjects();
        HasAnyRecentProjects = entries.Count > 0;

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? entries
            : entries.Where(e => e.FilePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        RecentProjects.Clear();
        // OrderByDescending is stable, so MRU order is preserved within pinned/unpinned groups
        foreach (var entry in filtered.OrderByDescending(e => e.Pinned))
        {
            RecentProjects.Add(new RecentProjectItemViewModel(entry));
        }
    }

    [RelayCommand]
    private async Task NewProject()
    {
        if (NewProjectRequested != null)
            await NewProjectRequested();
        RefreshIfStillVisible();
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (OpenProjectRequested != null)
            await OpenProjectRequested();
        RefreshIfStillVisible();
    }

    [RelayCommand]
    private async Task OpenRecentProject(RecentProjectItemViewModel? item)
    {
        if (item == null)
            return;

        // Re-check on click: the file may have been moved/deleted since the
        // list was built. Mark the row instead of failing the load.
        if (!File.Exists(item.FullPath))
        {
            item.IsMissing = true;
            return;
        }

        if (OpenProjectFromPathRequested != null)
            await OpenProjectFromPathRequested(item.FullPath);
        RefreshIfStillVisible();
    }

    [RelayCommand]
    private async Task OpenExample(ExampleDesign? example)
    {
        if (example == null || OpenExampleRequested == null)
            return;

        // Re-check on click: the examples tree may have changed since startup.
        // Drop a vanished example from the list instead of failing the load.
        if (!File.Exists(example.FilePath))
        {
            RefreshExamples();
            return;
        }

        await OpenExampleRequested(example.FilePath);
        RefreshIfStillVisible();
    }

    /// <summary>
    /// Re-syncs the recents list after a Home-initiated action that did NOT
    /// dismiss the card (e.g. the unsaved-changes prompt saved a design and the
    /// user then cancelled the picker) — otherwise the visible list goes stale.
    /// </summary>
    private void RefreshIfStillVisible()
    {
        if (IsHomeVisible)
            RefreshRecentProjects();
    }

    [RelayCommand]
    private async Task LearnTutorial()
    {
        if (LearnTutorialRequested != null)
            await LearnTutorialRequested();
    }

    [RelayCommand]
    private async Task WatchComputeTour()
    {
        if (WatchComputeTourRequested != null)
            await WatchComputeTourRequested();
    }

    [RelayCommand]
    private void TogglePin(RecentProjectItemViewModel? item)
    {
        if (item == null)
            return;

        _recentProjectsService.TogglePin(item.FullPath);
        RefreshRecentProjects();
    }

    [RelayCommand]
    private void RemoveRecentProject(RecentProjectItemViewModel? item)
    {
        if (item == null)
            return;

        _recentProjectsService.RemoveProject(item.FullPath);
        RefreshRecentProjects();
    }

    [RelayCommand]
    private void ClearRecentProjects()
    {
        _recentProjectsService.ClearAll();
        RefreshRecentProjects();
    }

    [RelayCommand]
    private void ContinueWithoutProject()
    {
        IsHomeVisible = false;
    }
}

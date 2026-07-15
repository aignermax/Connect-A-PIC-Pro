using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// ViewModel for managing loaded PDKs with filtering capabilities.
/// Displays loaded PDKs and allows toggling visibility of their components.
/// </summary>
public partial class PdkManagerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>
    /// True when the user may freely toggle individual PDKs on/off. Set to false while a real
    /// (non-Playground) process governs the enabled set (issue #570), so the per-PDK checkboxes
    /// in the PDK-manager UI can be disabled/hidden and reflect that the selection is locked.
    /// </summary>
    [ObservableProperty]
    private bool _manualTogglesEnabled = true;

    /// <summary>
    /// True while a bulk update (SetEnabledPdks / EnableAll / DisableAll) is toggling
    /// PDKs: the per-PDK PropertyChanged handler skips its per-item filter refresh so a
    /// batch of N toggles costs one re-filter (and one preferences write), not N+1.
    /// </summary>
    private bool _suppressFilterNotifications;

    /// <summary>
    /// Collection of all loaded PDK information.
    /// Each item tracks name, path, component count, and enabled state.
    /// </summary>
    public ObservableCollection<PdkInfoViewModel> LoadedPdks { get; } = new();

    /// <summary>
    /// Callback invoked when PDK filter state changes.
    /// Set by MainViewModel to trigger component library filtering.
    /// </summary>
    public Action? OnFilterChanged { get; set; }

    /// <summary>
    /// Registers a new PDK with the manager.
    /// </summary>
    /// <param name="name">PDK name.</param>
    /// <param name="filePath">Path to PDK file (null for built-in).</param>
    /// <param name="isBundled">True if bundled with application.</param>
    /// <param name="componentCount">Number of components in PDK.</param>
    public void RegisterPdk(string name, string? filePath, bool isBundled, int componentCount)
    {
        var pdkVm = new PdkInfoViewModel(name, filePath, isBundled, componentCount);
        pdkVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PdkInfoViewModel.IsEnabled) && !_suppressFilterNotifications)
            {
                OnFilterChanged?.Invoke();
                UpdateStatusText();
            }
        };
        LoadedPdks.Add(pdkVm);
        UpdateStatusText();
    }

    /// <summary>
    /// Checks if a PDK file is already loaded (duplicate detection).
    /// </summary>
    public bool IsPdkLoaded(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return LoadedPdks.Any(p => p.FilePath != null &&
                                   Path.GetFullPath(p.FilePath) == normalizedPath);
    }

    /// <summary>
    /// Checks if a PDK with the given name and source type exists.
    /// </summary>
    public bool IsPdkNameLoaded(string name, string? pdkSource)
    {
        return LoadedPdks.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                   (pdkSource == null || p.SourceType.Equals(pdkSource, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Enables all PDKs and refreshes the component library. Disabled while a process
    /// governs the enabled set (issue #570) — the bulk buttons must not override the
    /// same lock that greys out the per-PDK checkboxes.
    /// </summary>
    [RelayCommand]
    private void EnableAll()
    {
        var changed = SetAllEnabled(true);
        StatusText = $"Enabled {changed} allowed PDK(s)";
    }

    /// <summary>
    /// Disables every PDK the user may toggle (skipping process-locked ones) and refreshes the
    /// component library.
    /// </summary>
    [RelayCommand]
    private void DisableAll()
    {
        var changed = SetAllEnabled(false);
        StatusText = $"Disabled {changed} PDK(s)";
    }

    /// <summary>
    /// Sets the enabled state of every PDK the user may toggle — i.e. those NOT locked by the
    /// active fabrication process (a locked PDK belongs to a foreign process and keeps its
    /// process-dictated state). So "Enable all" turns on all allowed/compatible PDKs and
    /// "Disable all" turns them off, both without fighting the process lock. Returns the number
    /// of PDKs actually changed.
    /// </summary>
    private int SetAllEnabled(bool enabled)
    {
        var toggleable = LoadedPdks.Where(p => !p.IsLockedByProcess).ToList();
        _suppressFilterNotifications = true;
        try
        {
            foreach (var pdk in toggleable)
                pdk.IsEnabled = enabled;
        }
        finally
        {
            _suppressFilterNotifications = false;
        }
        OnFilterChanged?.Invoke();
        return toggleable.Count;
    }

    /// <summary>
    /// Removes a user-loaded PDK from the manager.
    /// Bundled PDKs cannot be unloaded.
    /// </summary>
    [RelayCommand]
    private void UnloadPdk(PdkInfoViewModel? pdk)
    {
        if (pdk == null || pdk.IsBundled) return;

        LoadedPdks.Remove(pdk);
        OnFilterChanged?.Invoke();
        StatusText = $"Unloaded: {pdk.Name}";
    }

    private void UpdateStatusText()
    {
        var enabledCount = LoadedPdks.Count(p => p.IsEnabled);
        StatusText = $"{enabledCount}/{LoadedPdks.Count} PDKs active";
    }

    /// <summary>
    /// Returns a list of enabled PDK names for filtering.
    /// </summary>
    public HashSet<string> GetEnabledPdkNames()
    {
        return LoadedPdks
            .Where(p => p.IsEnabled)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the names of loaded PDKs that are compatible with the active process lock —
    /// every PDK whose <see cref="PdkInfoViewModel.IsLockedByProcess"/> is false. When no process
    /// is active (<see cref="ClearProcessLock"/> ran, or a process was never applied), nothing is
    /// locked, so this returns every loaded PDK name.
    /// <para>
    /// Deliberately NOT the same as <see cref="GetEnabledPdkNames"/>: per
    /// <see cref="ApplyProcessLock"/>'s doc, a process MEMBER PDK's enabled checkbox stays
    /// user-togglable to declutter the component library — that is a filtering choice, not a
    /// process violation. Callers that need to judge whether a placed component's PDK still
    /// belongs to the active process (e.g. the design-check PDK-compatibility validator) must use
    /// this lock-based set; using <see cref="GetEnabledPdkNames"/> there would wrongly flag a
    /// manually-disabled-but-still-valid member PDK's components as process-conflicted.
    /// </para>
    /// </summary>
    public HashSet<string> GetProcessCompatiblePdkNames()
    {
        return LoadedPdks
            .Where(p => !p.IsLockedByProcess)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drives the enabled set directly: every loaded PDK whose name is in <paramref name="names"/>
    /// is enabled, all others disabled. Used to lock the library to the active process's member
    /// PDKs (issue #570); callers are expected to also set <see cref="ManualTogglesEnabled"/>.
    /// </summary>
    public void SetEnabledPdks(IEnumerable<string> names)
    {
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressFilterNotifications = true;
        try
        {
            foreach (var pdk in LoadedPdks)
            {
                pdk.IsEnabled = nameSet.Contains(pdk.Name);
            }
        }
        finally
        {
            _suppressFilterNotifications = false;
        }

        UpdateStatusText();
        OnFilterChanged?.Invoke();
    }

    /// <summary>
    /// Locks the library to a process (issue #570): PDKs in <paramref name="allowedNames"/>
    /// (process members + process-agnostic tools) start enabled and REMAIN individually
    /// toggleable — deselecting a member PDK to declutter the component library is a
    /// filtering choice, not a process violation. All other PDKs are disabled and their
    /// checkbox is locked, because enabling a foreign-process PDK would contradict the
    /// single-process rule.
    /// </summary>
    public void ApplyProcessLock(IEnumerable<string> allowedNames, bool preserveMemberToggles = false)
    {
        var allowed = allowedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressFilterNotifications = true;
        try
        {
            foreach (var pdk in LoadedPdks)
            {
                var isAllowed = allowed.Contains(pdk.Name);
                // When re-applying the lock after a PDK change, an already-unlocked member keeps
                // the user's manual declutter toggle; only a newly-added/previously-foreign PDK
                // gets enabled. Foreign PDKs are always disabled and locked.
                if (isAllowed)
                {
                    if (!preserveMemberToggles || pdk.IsLockedByProcess)
                        pdk.IsEnabled = true;
                }
                else
                {
                    pdk.IsEnabled = false;
                }
                pdk.IsLockedByProcess = !isAllowed;
            }
        }
        finally
        {
            _suppressFilterNotifications = false;
        }

        UpdateStatusText();
        OnFilterChanged?.Invoke();
    }

    /// <summary>Removes all per-PDK process locks (returning to Playground / no selection).</summary>
    public void ClearProcessLock()
    {
        foreach (var pdk in LoadedPdks)
            pdk.IsLockedByProcess = false;
    }
}

/// <summary>
/// ViewModel wrapper for PdkInfo that supports UI binding.
/// </summary>
public partial class PdkInfoViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>
    /// True when this PDK belongs to a foreign process while the design is locked
    /// (issue #570): its checkbox is disabled because enabling it would violate the
    /// single-process rule. Member/tool PDKs stay toggleable for library filtering.
    /// </summary>
    [ObservableProperty]
    private bool _isLockedByProcess;

    public string Name { get; }
    public string? FilePath { get; }
    public bool IsBundled { get; }
    public int ComponentCount { get; }
    public string SourceType => IsBundled ? "Bundled" : "User";

    public PdkInfoViewModel(string name, string? filePath, bool isBundled, int componentCount)
    {
        Name = name;
        FilePath = filePath;
        IsBundled = isBundled;
        ComponentCount = componentCount;
    }

    public string DisplayText => $"{Name} ({ComponentCount} components)";
    public string SourceBadge => IsBundled ? "📦 Bundled" : "📂 User";
}

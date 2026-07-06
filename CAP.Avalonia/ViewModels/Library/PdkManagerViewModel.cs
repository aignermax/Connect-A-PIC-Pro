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
            if (e.PropertyName == nameof(PdkInfoViewModel.IsEnabled))
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
    /// Enables all PDKs and refreshes the component library.
    /// </summary>
    [RelayCommand]
    private void EnableAll()
    {
        foreach (var pdk in LoadedPdks)
        {
            pdk.IsEnabled = true;
        }
        OnFilterChanged?.Invoke();
        StatusText = "All PDKs enabled";
    }

    /// <summary>
    /// Disables all PDKs and refreshes the component library.
    /// </summary>
    [RelayCommand]
    private void DisableAll()
    {
        foreach (var pdk in LoadedPdks)
        {
            pdk.IsEnabled = false;
        }
        OnFilterChanged?.Invoke();
        StatusText = "All PDKs disabled";
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
    /// Drives the enabled set directly: every loaded PDK whose name is in <paramref name="names"/>
    /// is enabled, all others disabled. Used to lock the library to the active process's member
    /// PDKs (issue #570); callers are expected to also set <see cref="ManualTogglesEnabled"/>.
    /// </summary>
    public void SetEnabledPdks(IEnumerable<string> names)
    {
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pdk in LoadedPdks)
        {
            pdk.IsEnabled = nameSet.Contains(pdk.Name);
        }

        OnFilterChanged?.Invoke();
    }
}

/// <summary>
/// ViewModel wrapper for PdkInfo that supports UI binding.
/// </summary>
public partial class PdkInfoViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Library;

public partial class PdkManagerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _manualTogglesEnabled = true;

    private bool _suppressFilterNotifications;

    public ObservableCollection<PdkInfoViewModel> LoadedPdks { get; } = new();

    public Action? OnFilterChanged { get; set; }

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

    public bool IsPdkLoaded(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return LoadedPdks.Any(p => p.FilePath != null &&
                                   Path.GetFullPath(p.FilePath) == normalizedPath);
    }

    public bool IsPdkNameLoaded(string name, string? pdkSource)
    {
        return LoadedPdks.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                   (pdkSource == null || p.SourceType.Equals(pdkSource, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private void EnableAll()
    {
        var changed = SetAllEnabled(true);
        StatusText = $"Enabled {changed} allowed PDK(s)";
    }

    [RelayCommand]
    private void DisableAll()
    {
        var changed = SetAllEnabled(false);
        StatusText = $"Disabled {changed} PDK(s)";
    }

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

    public HashSet<string> GetEnabledPdkNames()
    {
        return LoadedPdks
            .Where(p => p.IsEnabled)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> GetProcessCompatiblePdkNames()
    {
        return LoadedPdks
            .Where(p => !p.IsLockedByProcess)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

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

    public void ApplyProcessLock(IEnumerable<string> allowedNames, bool preserveMemberToggles = false)
    {
        var allowed = allowedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressFilterNotifications = true;
        try
        {
            foreach (var pdk in LoadedPdks)
            {
                var isAllowed = allowed.Contains(pdk.Name);
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

    public void ClearProcessLock()
    {
        foreach (var pdk in LoadedPdks)
            pdk.IsLockedByProcess = false;
    }
}

public partial class PdkInfoViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isLockedByProcess;

    /// <summary>
    /// True when this non-bundled PDK is the user's editable fork of a bundled PDK with the
    /// same name (OrdinalIgnoreCase) — it "shadows" the read-only built-in original. Deleting
    /// such a fork means "revert to the foundry truth": the copy goes to the trash and the
    /// bundled original is re-registered.
    /// </summary>
    [ObservableProperty]
    private bool _shadowsBundledPdk;

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

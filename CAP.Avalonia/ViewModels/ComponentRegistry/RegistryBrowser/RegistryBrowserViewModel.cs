using System.Collections.ObjectModel;
using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP_Core.ComponentRegistry.RegistryClient;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// Read-only browser for the open photonic component registry (issue #656).
/// Loads the index via <see cref="RegistryClient"/> (cache-first, offline
/// tolerant), lists components with tier badges and status chips, flags
/// components whose process differs from the active one, and shows manifest
/// details (parameters, artifact provenance) for the selected component.
/// </summary>
public partial class RegistryBrowserViewModel : ObservableObject
{
    private readonly RegistryClient _client;

    /// <summary>Registry components, ordered by name.</summary>
    public ObservableCollection<RegistryComponentItemViewModel> Components { get; } = new();

    /// <summary>Detail pane for the selected component.</summary>
    public RegistryComponentDetailsViewModel Details { get; } = new();

    /// <summary>True while the index is being fetched.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Non-empty when the index could not be loaded (non-blocking error state).</summary>
    [ObservableProperty]
    private string _errorMessage = "";

    /// <summary>Informational note about the data source (e.g. offline cache fallback).</summary>
    [ObservableProperty]
    private string _sourceNote = "";

    /// <summary>Currently selected component; drives the detail pane.</summary>
    [ObservableProperty]
    private RegistryComponentItemViewModel? _selectedComponent;

    /// <summary>
    /// Id of the active fabrication process (single-process model, #570).
    /// Components targeting a different process are flagged as mismatched.
    /// Null means no process is loaded — nothing is flagged.
    /// </summary>
    [ObservableProperty]
    private string? _activeProcessId;

    /// <summary>Panel expansion state; the first expansion triggers an initial load.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>In-flight index load; awaited by tests and the screenshot harness.</summary>
    public Task IndexLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>In-flight detail load; awaited by tests and the screenshot harness.</summary>
    public Task DetailsLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Creates the browser on top of a configured registry client.</summary>
    public RegistryBrowserViewModel(RegistryClient client)
    {
        _client = client;
    }

    /// <summary>Loads the registry index (cache-first).</summary>
    [RelayCommand]
    private Task Load() => IndexLoadTask = LoadCoreAsync(forceRefresh: false);

    /// <summary>Re-downloads the registry index, bypassing the local cache.</summary>
    [RelayCommand]
    private Task Refresh() => IndexLoadTask = LoadCoreAsync(forceRefresh: true);

    private async Task LoadCoreAsync(bool forceRefresh)
    {
        if (IsLoading)
            return;

        IsLoading = true;
        ErrorMessage = "";
        SourceNote = "";
        var result = await _client.GetIndexAsync(forceRefresh);
        ApplyIndexResult(result, forceRefresh);
        IsLoading = false;
    }

    private void ApplyIndexResult(RegistryResult<RegistryIndex> result, bool forceRefresh)
    {
        if (!result.IsSuccess)
        {
            // Non-blocking: keep whatever is already listed and surface the reason.
            ErrorMessage = string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("Registry.LoadFailed"), result.ErrorMessage);
            return;
        }

        SelectedComponent = null;
        Components.Clear();
        foreach (var entry in result.Value!.Components.OrderBy(c => c.Name))
        {
            var item = new RegistryComponentItemViewModel(entry);
            item.UpdateProcessMismatch(ActiveProcessId);
            Components.Add(item);
        }

        if (result.Source == RegistrySource.Cache)
            SourceNote = LocalizationService.Instance.Translate(
                forceRefresh ? "Registry.OfflineCache" : "Registry.LoadedFromCache");
    }

    partial void OnActiveProcessIdChanged(string? value)
    {
        foreach (var item in Components)
            item.UpdateProcessMismatch(value);
    }

    partial void OnSelectedComponentChanged(RegistryComponentItemViewModel? value)
    {
        if (value is null)
            Details.Clear();
        else
            DetailsLoadTask = Details.LoadAsync(_client, value.ManifestPath);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        // Lazy first load: fetching only when the user opens the section keeps
        // application startup free of network/disk work for this panel.
        if (value && Components.Count == 0 && !IsLoading)
            IndexLoadTask = LoadCoreAsync(forceRefresh: false);
    }
}

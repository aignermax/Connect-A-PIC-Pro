using System.Collections.ObjectModel;
using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP_Core.ComponentRegistry.RegistryClient;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// Read-only browser for the open photonic component registry (issue #656),
/// hosted in the "Component Registry" tool window. Loads the index via
/// <see cref="RegistryClient"/> (cache-first, offline tolerant), lists
/// components as tiles with tier badges and status chips, filters by free
/// text / process / trust status, flags components whose process differs
/// from the active one, and shows manifest details (parameters, artifact
/// provenance) for the selected component.
/// </summary>
public partial class RegistryBrowserViewModel : ObservableObject
{
    private readonly RegistryClient _client;

    /// <summary>All registry components, ordered by name (unfiltered).</summary>
    public ObservableCollection<RegistryComponentItemViewModel> Components { get; } = new();

    /// <summary>Components matching the current search / process / status filters.</summary>
    public ObservableCollection<RegistryComponentItemViewModel> FilteredComponents { get; } = new();

    /// <summary>Process dropdown options: an "All processes" entry plus the distinct processes in the index.</summary>
    public ObservableCollection<RegistryFilterOption> ProcessFilters { get; } = new();

    /// <summary>Status dropdown options: an "All statuses" entry plus the distinct trust statuses in the index.</summary>
    public ObservableCollection<RegistryFilterOption> StatusFilters { get; } = new();

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

    /// <summary>Free-text filter matched against component name and description.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Selected process dropdown entry; a null <see cref="RegistryFilterOption.Value"/> shows all.</summary>
    [ObservableProperty]
    private RegistryFilterOption? _selectedProcessFilter;

    /// <summary>Selected status dropdown entry; a null <see cref="RegistryFilterOption.Value"/> shows all.</summary>
    [ObservableProperty]
    private RegistryFilterOption? _selectedStatusFilter;

    /// <summary>True when the index has components but none match the current filters.</summary>
    [ObservableProperty]
    private bool _hasNoResults;

    /// <summary>In-flight index load; awaited by tests and the screenshot harness.</summary>
    public Task IndexLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>In-flight detail load; awaited by tests and the screenshot harness.</summary>
    public Task DetailsLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Creates the browser on top of a configured registry client.</summary>
    public RegistryBrowserViewModel(RegistryClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Loads the index on first use (called when the registry window opens);
    /// no-op when components are already listed or a load is in flight. Keeps
    /// application startup free of network/disk work for this feature.
    /// </summary>
    public void EnsureLoaded()
    {
        if (Components.Count == 0 && !IsLoading)
            IndexLoadTask = LoadCoreAsync(forceRefresh: false);
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

        RebuildFilterOptions();
        ApplyFilters();

        if (result.Source == RegistrySource.Cache)
            SourceNote = LocalizationService.Instance.Translate(
                forceRefresh ? "Registry.OfflineCache" : "Registry.LoadedFromCache");
    }

    /// <summary>
    /// Rebuilds both filter dropdowns from the loaded index, keeping the current
    /// selection when its value still exists (e.g. after a refresh).
    /// </summary>
    private void RebuildFilterOptions()
    {
        RebuildOptions(ProcessFilters, "Registry.AllProcesses",
            Components.Select(c => c.ProcessId), SelectedProcessFilter,
            option => SelectedProcessFilter = option);
        RebuildOptions(StatusFilters, "Registry.AllStatuses",
            Components.Select(c => c.Status), SelectedStatusFilter,
            option => SelectedStatusFilter = option);
    }

    private static void RebuildOptions(
        ObservableCollection<RegistryFilterOption> options,
        string allEntryKey,
        IEnumerable<string> values,
        RegistryFilterOption? previousSelection,
        Action<RegistryFilterOption> select)
    {
        options.Clear();
        options.Add(new RegistryFilterOption(null, LocalizationService.Instance.Translate(allEntryKey)));
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v))
            options.Add(new RegistryFilterOption(value, value));

        select(options.FirstOrDefault(o =>
            string.Equals(o.Value, previousSelection?.Value, StringComparison.OrdinalIgnoreCase)) ?? options[0]);
    }

    /// <summary>Recomputes <see cref="FilteredComponents"/> from the current filters.</summary>
    private void ApplyFilters()
    {
        FilteredComponents.Clear();
        foreach (var item in Components.Where(MatchesFilters))
            FilteredComponents.Add(item);

        HasNoResults = FilteredComponents.Count == 0 && Components.Count > 0;
        if (SelectedComponent is not null && !FilteredComponents.Contains(SelectedComponent))
            SelectedComponent = null;
    }

    private bool MatchesFilters(RegistryComponentItemViewModel item)
    {
        if (SelectedProcessFilter?.Value is { } process
            && !string.Equals(item.ProcessId, process, StringComparison.OrdinalIgnoreCase))
            return false;
        if (SelectedStatusFilter?.Value is { } status
            && !string.Equals(item.Status, status, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        return item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedProcessFilterChanged(RegistryFilterOption? value) => ApplyFilters();

    partial void OnSelectedStatusFilterChanged(RegistryFilterOption? value) => ApplyFilters();

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
}

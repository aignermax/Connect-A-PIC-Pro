using System.Collections.ObjectModel;
using System.Globalization;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Creation;
using CAP_Core.Components;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly PdkLoader _pdkLoader;
    private readonly UserPreferencesService _preferencesService;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly AddCustomComponentDependencies? _addCustomComponentDeps;

    private readonly List<PdkDraft> _loadedPdkDrafts = new();

    public HierarchyPanelViewModel HierarchyPanel { get; }

    public PdkManagerViewModel PdkManager { get; }

    public ComponentLibraryViewModel ComponentLibrary { get; }

    public ObservableCollection<ComponentTemplate> AllTemplates { get; } = new();

    public ObservableCollection<ComponentTemplate> FilteredTemplates { get; } = new();

    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private double _libraryScrollOffset = 0.0;

    [ObservableProperty]
    private GroupTemplate? _selectedGroupTemplate;

    private GridLength _leftPanelWidth = new GridLength(220);
    public GridLength LeftPanelWidth
    {
        get => _leftPanelWidth;
        set
        {
            var clampedValue = Math.Max(200, Math.Min(800, value.Value));
            var newGridLength = new GridLength(clampedValue);
            if (SetProperty(ref _leftPanelWidth, newGridLength))
            {
                SaveLeftPanelWidth();
            }
        }
    }

    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// Optional status sink that keeps the string-table key: (key, format args). Preferred
    /// over <see cref="UpdateStatus"/> for localized messages so the host can re-translate
    /// them on a live UI language switch (field bug round 5).
    /// </summary>
    public Action<string, object[]>? UpdateLocalizedStatus { get; set; }

    public IFileDialogService? FileDialogService { get; set; }

    public Action<GroupTemplate>? OnGroupTemplateSelected { get; set; }

    public Func<string, Task<string?>>? ShowImportWizardAsync { get; set; }

    public Func<CAP.Avalonia.ViewModels.Components.AddCustomComponent.NewComponentViewModel, Task>? ShowNewComponentWindowAsync { get; set; }
    public LeftPanelViewModel(
        DesignCanvasViewModel canvas,
        GroupLibraryManager libraryManager,
        PdkLoader pdkLoader,
        UserPreferencesService preferencesService,
        HierarchyPanelViewModel hierarchyPanel,
        PdkManagerViewModel pdkManager,
        ComponentLibraryViewModel componentLibrary,
        ErrorConsoleService? errorConsole = null,
        AddCustomComponentDependencies? addCustomComponentDeps = null)
    {
        _canvas = canvas;
        _pdkLoader = pdkLoader;
        _preferencesService = preferencesService;
        _errorConsole = errorConsole;
        _addCustomComponentDeps = addCustomComponentDeps;

        HierarchyPanel = hierarchyPanel;
        PdkManager = pdkManager;
        ComponentLibrary = componentLibrary;

        PdkManager.OnFilterChanged = FilterComponents;
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo UnitTests): when set, <see cref="Initialize"/> scans this
    /// directory for user PDKs instead of the real per-user user-pdks folder, so headless UI
    /// tests never pick up (or write into) the developer's real forks.
    /// </summary>
    internal string? UserPdkStartupRootOverride { get; set; }

    public void Initialize()
    {
        LoadComponentLibrary();
        RestorePdkFilterState();
        RestoreLeftPanelWidth();

        try
        {
            _ = ReloadUserPdksAtStartupAsync(UserPdkStartupRootOverride);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to reload user PDKs at startup: {ex.Message}", ex);
        }
    }

    partial void OnSearchTextChanged(string value) => FilterComponents();

    partial void OnSelectedGroupTemplateChanged(GroupTemplate? value)
    {
        if (value != null)
        {
            OnGroupTemplateSelected?.Invoke(value);
        }
    }

    private void LoadComponentLibrary()
    {
        LoadBundledPdks();

        var categories = AllTemplates.Select(t => t.Category).Distinct().OrderBy(c => c);
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        // Keep the key: this is the status visible right after startup, so it must
        // re-translate when the user switches the UI language in Settings.
        if (UpdateLocalizedStatus != null)
            UpdateLocalizedStatus("Status.LoadedComponentTypes", [AllTemplates.Count]);
        else
            UpdateStatus?.Invoke(string.Format(
                CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("Status.LoadedComponentTypes"),
                AllTemplates.Count));
        FilterComponents();
    }

    private void LoadBundledPdks()
    {
        var pdkDir = ResolveBundledPdkDirectory(AppDomain.CurrentDomain.BaseDirectory);
        if (pdkDir == null) return;

        LoadBundledPdksFrom(pdkDir);
    }

    /// <summary>
    /// Loads every bundled PDK JSON from <paramref name="pdkDir"/> and records each in the
    /// bundled-origin catalog. A user fork on disk does NOT suppress the bundled load here —
    /// the startup reload replaces (shadows) the bundled entry instead, which keeps the
    /// built-in PDK available when the fork file turns out to be unreadable.
    /// </summary>
    internal void LoadBundledPdksFrom(string pdkDir)
    {
        foreach (var pdkFile in Directory.GetFiles(pdkDir, "*.json"))
        {
            try
            {
                var pdk = _pdkLoader.LoadFromFile(pdkFile);
                _loadedPdkDrafts.Add(pdk);
                int componentCount = 0;
                foreach (var pdkComp in pdk.Components)
                {
                    var template = ConvertPdkComponentToTemplate(
                        pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection, pdk.Process);
                    template.IsCustom = false;
                    AllTemplates.Add(template);
                    componentCount++;
                }

                PdkManager.RegisterPdk(pdk.Name, pdkFile, true, componentCount);
                RecordBundledPdkOrigin(pdk.Name, pdkFile, componentCount);
            }
            catch (CAP_DataAccess.Components.ComponentDraftMapper.PdkValidationException vex)
            {
                foreach (var error in vex.Errors)
                {
                    _errorConsole?.LogError($"PDK validation: {error}");
                }
                _errorConsole?.LogError($"Skipped PDK '{Path.GetFileName(pdkFile)}' — {vex.Errors.Count} validation error(s)");
            }
            catch (Exception ex)
            {
                _errorConsole?.LogError($"Failed to load PDK '{Path.GetFileName(pdkFile)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolution moved to <see cref="CAP_DataAccess.Components.ComponentDraftMapper.BundledPdkPaths"/>
    /// so the data-access layer (PdkJsonSaver write guard) shares the same notion of
    /// "bundled directory" as the library load path. Facade kept for existing callers/tests.
    /// </summary>
    internal static string? ResolveBundledPdkDirectory(string baseDir) =>
        CAP_DataAccess.Components.ComponentDraftMapper.BundledPdkPaths.ResolveBundledPdkDirectory(baseDir);

    private void FilterComponents()
    {
        FilteredTemplates.Clear();
        var query = SearchText?.Trim() ?? "";
        var enabledPdks = PdkManager.GetEnabledPdkNames();

        var candidates = AllTemplates
            .Where(t => enabledPdks.Contains(t.PdkSource))
            .Where(t => query.Length == 0 || MatchesSearch(t, query))
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var t in candidates)
            FilteredTemplates.Add(t);

        SavePdkFilterState();
    }

    private static bool MatchesSearch(ComponentTemplate t, string query)
    {
        return t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || t.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (t.NazcaFunctionName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || t.PdkSource.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void RefreshUserGlobalOverrideBadges(Func<string, bool> hasUserOverride)
    {
        foreach (var t in AllTemplates)
        {
            var key = $"{t.PdkSource}::{t.Name}";
            t.HasUserGlobalSMatrixOverride = hasUserOverride(key);
        }
    }

    private void RestorePdkFilterState()
    {
        var enabledPdks = _preferencesService.GetEnabledPdks();

        if (enabledPdks.Count == 0)
            return;

        var knownPdks = _preferencesService.GetKnownPdks();
        foreach (var pdk in PdkManager.LoadedPdks)
        {
            // Only a KNOWN name absent from the enabled set was deliberately unchecked; an
            // unknown PDK keeps its default enabled state. Empty known list = legacy prefs.
            if (knownPdks.Count > 0 && !knownPdks.Contains(pdk.Name))
                continue;
            pdk.IsEnabled = enabledPdks.Contains(pdk.Name);
        }

        FilterComponents();
    }

    private void SavePdkFilterState()
    {
        // A process-locked enable set is derived state; persisting it would overwrite the
        // user's own manual PDK selection.
        if (!PdkManager.ManualTogglesEnabled)
            return;

        _preferencesService.SetPdkFilterState(
            PdkManager.GetEnabledPdkNames(),
            PdkManager.LoadedPdks.Select(p => p.Name));
    }

    private void RestoreLeftPanelWidth()
    {
        var width = _preferencesService.GetLeftPanelWidth();
        LeftPanelWidth = new GridLength(width);
    }

    private void SaveLeftPanelWidth()
    {
        _preferencesService.SetLeftPanelWidth(LeftPanelWidth.Value);
    }

    [RelayCommand]
    private async Task LoadPdk()
    {
        if (FileDialogService == null) return;

        var filePath = await FileDialogService.ShowOpenFileDialogAsync(
            "Open PDK",
            "PDK Files (*.json;*.py)|*.json;*.py|PDK JSON (*.json)|*.json|Nazca Python (*.py)|*.py|All Files (*.*)|*.*");

        if (string.IsNullOrEmpty(filePath)) return;

        if (filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPdkFromPythonFileAsync(filePath);
            return;
        }

        await LoadPdkFromJsonFileAsync(filePath);
    }

    private async Task LoadPdkFromPythonFileAsync(string pyFilePath)
    {
        if (ShowImportWizardAsync == null)
        {
            UpdateStatus?.Invoke("PDK Import Wizard is not available in this context.");
            return;
        }

        UpdateStatus?.Invoke($"Opening PDK Import Wizard for '{Path.GetFileName(pyFilePath)}'...");
        var savedJsonPath = await ShowImportWizardAsync(pyFilePath);

        if (string.IsNullOrEmpty(savedJsonPath)) return;

        await LoadPdkFromJsonFileAsync(savedJsonPath);
    }

    private async Task LoadPdkFromJsonFileAsync(string filePath)
    {
        if (PdkManager.IsPdkLoaded(filePath))
        {
            UpdateStatus?.Invoke("PDK already loaded from this file");
            return;
        }

        try
        {
            var pdk = _pdkLoader.LoadFromFile(filePath);

            if (PdkManager.IsPdkNameLoaded(pdk.Name, null))
            {
                // A file named like a loaded BUNDLED PDK is the user's fork and shadows the
                // built-in original — same semantics as the startup reload. The file parsed
                // successfully above, so deregistering here cannot strand the library without
                // either entry. Any other name collision is still rejected.
                var shadowedBundled = PdkManager.LoadedPdks.FirstOrDefault(p =>
                    p.IsBundled && p.Name.Equals(pdk.Name, StringComparison.OrdinalIgnoreCase));
                if (shadowedBundled is null)
                {
                    UpdateStatus?.Invoke($"PDK '{pdk.Name}' is already loaded");
                    return;
                }
                DeregisterBundledPdkForShadow(shadowedBundled);
            }

            _loadedPdkDrafts.Add(pdk);

            int addedCount = 0;
            foreach (var pdkComp in pdk.Components)
            {
                var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName, process: pdk.Process);
                template.IsCustom = true;
                AllTemplates.Add(template);
                if (!Categories.Contains(template.Category))
                    Categories.Add(template.Category);
                addedCount++;
            }

            PdkManager.RegisterPdk(pdk.Name, filePath, false, addedCount);
            MarkIfShadowsBundledPdk(pdk.Name);
            _preferencesService.AddUserPdkPath(filePath);

            ReapplyActiveProcessAfterPdkChange();
            FilterComponents();
            UpdateStatus?.Invoke($"Loaded PDK '{pdk.Name}' with {addedCount} components");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to load PDK: {ex.Message}", ex);
            UpdateStatus?.Invoke($"Failed to load PDK: {ex.Message}");
        }
    }

    private static ComponentTemplate ConvertPdkComponentToTemplate(
        PdkComponentDraft pdkComp, string pdkName, string? nazcaModuleName,
        string? gdsFactoryRoutingCrossSection = null,
        CAP_DataAccess.Components.ComponentDraftMapper.DTOs.ProcessDefinition? process = null)
        => PdkTemplateConverter.ConvertToTemplate(
            pdkComp, pdkName, nazcaModuleName, gdsFactoryRoutingCrossSection, process);
}

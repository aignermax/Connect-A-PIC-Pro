using System.Collections.ObjectModel;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Components.Creation;
using CAP_Core.Components;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// ViewModel for the left sidebar panel.
/// Contains hierarchy panel, component library management, and PDK loading.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class LeftPanelViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly PdkLoader _pdkLoader;
    private readonly UserPreferencesService _preferencesService;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly AddCustomComponentDependencies? _addCustomComponentDeps;

    /// <summary>
    /// Every PDK loaded into the library so far (bundled + user-imported), kept so
    /// <see cref="GetLoadedPdkProcessEntries"/> can derive process fingerprints for
    /// the single-process catalog (issue #570).
    /// </summary>
    private readonly List<PdkDraft> _loadedPdkDrafts = new();

    /// <summary>
    /// ViewModel for the hierarchy panel showing component tree structure.
    /// </summary>
    public HierarchyPanelViewModel HierarchyPanel { get; }

    /// <summary>
    /// ViewModel for PDK management (loading, filtering, enabling/disabling PDKs).
    /// </summary>
    public PdkManagerViewModel PdkManager { get; }

    /// <summary>
    /// ViewModel for managing saved ComponentGroup templates.
    /// </summary>
    public ComponentLibraryViewModel ComponentLibrary { get; }

    /// <summary>
    /// All component templates (built-in + PDK).
    /// </summary>
    public ObservableCollection<ComponentTemplate> AllTemplates { get; } = new();

    /// <summary>
    /// Filtered component templates based on search and PDK filters.
    /// </summary>
    public ObservableCollection<ComponentTemplate> FilteredTemplates { get; } = new();

    /// <summary>
    /// Available component categories.
    /// </summary>
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private double _libraryScrollOffset = 0.0;

    [ObservableProperty]
    private GroupTemplate? _selectedGroupTemplate;

    private GridLength _leftPanelWidth = new GridLength(220);
    /// <summary>
    /// Width of the left panel in pixels. Persisted in user preferences.
    /// Clamped to [200, 800] range.
    /// </summary>
    public GridLength LeftPanelWidth
    {
        get => _leftPanelWidth;
        set
        {
            // Clamp to reasonable values (min 200, max 800)
            var clampedValue = Math.Max(200, Math.Min(800, value.Value));
            var newGridLength = new GridLength(clampedValue);
            if (SetProperty(ref _leftPanelWidth, newGridLength))
            {
                SaveLeftPanelWidth();
            }
        }
    }

    /// <summary>
    /// Callback to update status text in the UI.
    /// </summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// File dialog service for loading PDK files.
    /// </summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Callback invoked when a group template is selected for placement.
    /// </summary>
    public Action<GroupTemplate>? OnGroupTemplateSelected { get; set; }

    /// <summary>
    /// Async callback to show the PDK Import Wizard for a Python .py file.
    /// Set by the view layer (MainWindow.axaml.cs).
    /// Returns the saved JSON file path on success, or null if cancelled.
    /// </summary>
    public Func<string, Task<string?>>? ShowImportWizardAsync { get; set; }

    /// <summary>Shows the "New Component" window (non-modal; set by MainWindow.axaml.cs).</summary>
    public Func<CAP.Avalonia.ViewModels.Components.AddCustomComponent.NewComponentViewModel, Task>? ShowNewComponentWindowAsync { get; set; }
    /// <summary>Initializes a new instance of <see cref="LeftPanelViewModel"/>.</summary>
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
    /// Initializes the component library (loads built-in + bundled PDKs).
    /// </summary>
    public void Initialize()
    {
        LoadComponentLibrary();
        RestorePdkFilterState();
        RestoreLeftPanelWidth();

        // Fire-and-forget (issue #700): ReloadUserPdksAtStartupAsync does its file I/O
        // synchronously and returns an already-completed Task, so this executes fully on the
        // calling (UI) thread before Initialize() returns — no cross-thread mutation of the
        // observable AllTemplates/LoadedPdks collections. Wrapped in try/catch here (rather than
        // awaited) because Initialize() itself is synchronous and must not throw on a single bad
        // user PDK file; ReloadUserPdksAtStartupAsync already tolerates per-file failures
        // internally, this is a last-resort guard against anything unexpected.
        try
        {
            _ = ReloadUserPdksAtStartupAsync();
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
        // All components are loaded from JSON PDK files — no hardcoded built-in templates.
        LoadBundledPdks();

        // Build category list from all loaded templates
        var categories = AllTemplates.Select(t => t.Category).Distinct().OrderBy(c => c);
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        UpdateStatus?.Invoke($"Loaded {AllTemplates.Count} component types");
        FilterComponents();
    }

    private void LoadBundledPdks()
    {
        var pdkDir = ResolveBundledPdkDirectory(AppDomain.CurrentDomain.BaseDirectory);
        if (pdkDir == null) return;

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
                        pdkComp, pdk.Name, pdk.NazcaModuleName, pdk.GdsFactoryRoutingCrossSection);
                    // Bundled PDK — never editable (Foundry read-only).
                    template.IsCustom = false;
                    AllTemplates.Add(template);
                    componentCount++;
                }

                PdkManager.RegisterPdk(pdk.Name, pdkFile, true, componentCount);
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
    /// Resolves which PDK directory to load and save against. In a dev build —
    /// running from <c>bin/Debug</c> or <c>bin/Release</c> inside the source
    /// tree — prefers the repo-tracked <c>CAP-DataAccess/PDKs</c> sibling so
    /// the offset editor's saves land in the git working tree instead of the
    /// build artefact (which the next build would silently overwrite and
    /// which is never committed). Falls back to the bundled copy next to the
    /// executable for deployed builds.
    /// </summary>
    /// <remarks>Internal so unit tests can drive it with a fake start dir.</remarks>
    internal static string? ResolveBundledPdkDirectory(string baseDir)
    {
        var bundled = Path.Combine(baseDir, "PDKs");

        // Walk up to the repo root looking for a CAP-DataAccess/PDKs sibling.
        // 6 levels covers bin/<config>/<tfm>/<runtime>/ plus the project dir.
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "CAP-DataAccess", "PDKs");
            if (Directory.Exists(candidate) &&
                Directory.GetFiles(candidate, "*.json").Length > 0)
                return candidate;
        }

        return Directory.Exists(bundled) ? bundled : null;
    }

    private void FilterComponents()
    {
        FilteredTemplates.Clear();
        var query = SearchText?.Trim() ?? "";
        var enabledPdks = PdkManager.GetEnabledPdkNames();

        // Sort by category first so the flat ListBox visually groups
        // components of the same kind; secondary sort by name within
        // each category. Analysis tools land in the "Analysis" group.
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

    /// <summary>
    /// Updates the <see cref="ComponentTemplate.HasUserGlobalSMatrixOverride"/> flag on
    /// every template, so the 📊 badge in the PDK list reflects the current state of
    /// the user-global override store. The lookup uses the same key shape the dialog
    /// writes (<c>"{PdkSource}::{Name}"</c>); callers pass that as a predicate so this
    /// VM stays unaware of the store implementation.
    /// </summary>
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

        foreach (var pdk in PdkManager.LoadedPdks)
        {
            pdk.IsEnabled = enabledPdks.Contains(pdk.Name);
        }

        FilterComponents();
    }

    private void SavePdkFilterState()
    {
        // A process-locked enable set is derived state (issue #570) — persisting it
        // would permanently overwrite the user's own manual PDK selection.
        if (!PdkManager.ManualTogglesEnabled)
            return;

        var enabledPdks = PdkManager.GetEnabledPdkNames();
        _preferencesService.SetEnabledPdks(enabledPdks);
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

        // Python file: open the Import Wizard to parse and convert it first
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

        if (string.IsNullOrEmpty(savedJsonPath)) return; // User cancelled

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
                UpdateStatus?.Invoke($"PDK '{pdk.Name}' is already loaded");
                return;
            }

            _loadedPdkDrafts.Add(pdk);

            int addedCount = 0;
            foreach (var pdkComp in pdk.Components)
            {
                var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName);
                // User-loaded PDK — editable via the library's "Edit…" action.
                template.IsCustom = true;
                AllTemplates.Add(template);
                if (!Categories.Contains(template.Category))
                    Categories.Add(template.Category);
                addedCount++;
            }

            PdkManager.RegisterPdk(pdk.Name, filePath, false, addedCount);
            _preferencesService.AddUserPdkPath(filePath);

            // A PDK imported while a process is locked must not escape the lock:
            // re-apply so a foreign PDK registers disabled (issue #570).
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
        string? gdsFactoryRoutingCrossSection = null)
        => PdkTemplateConverter.ConvertToTemplate(
            pdkComp, pdkName, nazcaModuleName, gdsFactoryRoutingCrossSection);

    // OpenNewComponent, RegisterSavedCustomComponent, CanEditTemplate, EditCustomComponent:
    // see LeftPanelViewModel.AddCustomComponent.cs (kept out of this file to respect the
    // project's per-file line-count limit).

    // GetLoadedPdkProcessEntries, GetLoadedPdkDrafts, GetProcessAgnosticPdkNames,
    // ApplyActiveProcess, ResolveLiveMemberPdkNames, ReapplyActiveProcessAfterPdkChange:
    // see LeftPanelViewModel.ProcessLock.cs (same line-count reason as above).
}

using System.Collections.ObjectModel;
using System.Text.Json;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using CAP_Core.Routing;
using CAP_DataAccess.Persistence;
using CAP_DataAccess.Persistence.PIR;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP.Avalonia.ViewModels.Converters;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Export;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// ViewModel for file operations (save, load, export).
/// Handles all design file I/O and export functionality.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class FileOperationsViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager _commandManager;
    private readonly SimpleNazcaExporter _nazcaExporter;
    private readonly SaxExporter _saxExporter;
    private readonly ObservableCollection<ComponentTemplate> _componentLibrary;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly UserSMatrixOverrideStore? _userSMatrixOverrideStore;
    private readonly IUrlLauncher _urlLauncher;
    private readonly RecentProjectsService? _recentProjects;

    /// <summary>
    /// Current .lun format version this build reads and writes. Files with any other value are rejected at load time.
    /// </summary>
    private const string CurrentFormatVersion = "2.0";

    /// <summary>
    /// Absolute path of the currently open .lun file, or null for an unsaved
    /// new project. Observable so the Home screen and window title can react.
    /// </summary>
    [ObservableProperty]
    private string? _currentFilePath;

    /// <summary>Prompt shown before discarding unsaved changes to load another design.</summary>
    private const string LoadPromptMessage = "Do you want to save your changes before loading another design?";

    /// <summary>
    /// Persists metadata loaded from the last opened file so that Created date
    /// and other user-set fields survive a save-over-reload cycle.
    /// </summary>
    private DesignMetadata? _loadedMetadata;

    /// <summary>
    /// Names of components whose pin calibration changed since the loaded design was
    /// saved (a cached route docked against the pin's current angle and was discarded).
    /// Collected during connection load, reported once per component afterwards
    /// (round-5 review [2] — e.g. the DC-Halfring port-angle correction).
    /// </summary>
    private readonly HashSet<string> _pinCalibrationMigratedComponents = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-component S-matrix overrides loaded from the PIR section of the .lun file,
    /// or added via the S-parameter import feature. Survives save-over-reload cycles.
    /// Keyed by component identifier string; values are the stored S-matrices.
    /// </summary>
    public Dictionary<string, ComponentSMatrixData> StoredSMatrices { get; } = new();

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>
    /// The fabrication process this design is currently locked to (issue #570).
    /// Null means no components have been placed yet. Set via <see cref="SetActiveProcess"/>
    /// (from the New-Design dialog or the process picker), restored on load, or migrated
    /// from a legacy file's placed-component PDK sources.
    /// </summary>
    [ObservableProperty]
    private ActiveProcessSelection? _activeProcess;

    /// <summary>
    /// Supplies the current set of installable/loaded process groups. Wired by DI/MainViewModel
    /// to the live PDK catalog; used to migrate legacy files that predate single-process support.
    /// </summary>
    public Func<IReadOnlyList<ProcessGroup>>? ProcessCatalogProvider { get; set; }

    /// <summary>
    /// Supplies the process-derived metal routing parameters (trace width, GDS layer,
    /// crossing policy) for electrical connections at export time (issue #682). Wired by
    /// MainViewModel to the active process' PDK definitions; null falls back to
    /// <see cref="CAP_Core.Routing.MetalRouting.MetalRoutingSpec.Default"/>.
    /// </summary>
    public Func<CAP_Core.Routing.MetalRouting.MetalRoutingSpec>? MetalRoutingSpecProvider { get; set; }

    /// <summary>
    /// Supplies the names of loaded PDKs flagged process-agnostic (e.g. "Analysis Tools").
    /// Wired by DI/MainViewModel; passed to <see cref="ActiveProcessResolver.Migrate"/> so
    /// analyzer-only tool PDKs never count toward a legacy design's process attribution
    /// (issue #570 final review).
    /// </summary>
    public Func<IReadOnlyCollection<string>>? ProcessAgnosticPdkNamesProvider { get; set; }

    /// <summary>
    /// Callback invoked when loading a legacy file requires falling back to Playground
    /// because its components could not be attributed to a single installed process.
    /// </summary>
    public Action<string>? OnProcessMigrationWarning { get; set; }

    /// <summary>
    /// The open design's GDS-imported component sets (issue #830). Wired by
    /// MainViewModel; null in headless contexts. Captured into the .lun on
    /// save, restored (with legacy global import-PDK migration) on load, and
    /// cleared on New Project so imported components never leak across designs.
    /// </summary>
    public Services.GdsImport.DesignScope.DesignScopedGdsComponentService? DesignScopedGdsComponents { get; set; }

    /// <summary>
    /// Per-layer visibility of imported GDS geometry (issue #858). Wired by
    /// MainViewModel; null in headless contexts. Captured into the .lun on
    /// save, restored on load, and reset on New Project.
    /// </summary>
    public GdsImport.LayerVisibility.GdsLayerVisibilityViewModel? LayerVisibility { get; set; }

    /// <summary>
    /// ViewModel for GDS export functionality.
    /// </summary>
    public GdsExportViewModel GdsExport { get; }

    /// <summary>
    /// ViewModel for PhotonTorch export functionality.
    /// </summary>
    public PhotonTorchExportViewModel PhotonTorchExport { get; }

    /// <summary>
    /// ViewModel for Verilog-A / SPICE export functionality. Shared singleton
    /// so this property and the export-options dialog (VerilogAExportDialog,
    /// wired via <c>Views.Dialogs.ExportDialogWiring</c>) see the same state.
    /// </summary>
    public VerilogAExportViewModel VerilogAExport { get; }

    /// <summary>
    /// Callback to update status text in the UI.
    /// </summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// Callback invoked after a project is successfully opened or created
    /// (load from file or File → New). The Home screen uses this to dismiss itself.
    /// </summary>
    public Action? ProjectOpened { get; set; }

    /// <summary>
    /// Callback to rebuild hierarchy tree after loading.
    /// </summary>
    public Action? RebuildHierarchy { get; set; }

    /// <summary>
    /// Callback to trigger zoom-to-fit after loading.
    /// </summary>
    public Action<double, double>? ZoomToFitAfterLoad { get; set; }

    /// <summary>
    /// Callback to apply a chip size (in micrometers) after loading a project.
    /// Parameters: (widthMicrometers, heightMicrometers).
    /// </summary>
    public Action<double, double>? ApplyChipSizeAfterLoad { get; set; }

    /// <summary>
    /// File dialog service for showing open/save dialogs.
    /// </summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Message box service for showing confirmation dialogs.
    /// </summary>
    public IMessageBoxService? MessageBoxService { get; set; }

    /// <summary>
    /// Opens the Settings window, optionally pre-selecting a page by type.
    /// Wired by <see cref="MainViewModel"/>; null in headless contexts.
    /// </summary>
    public Func<Type?, Task>? ShowSettingsWindow { get; set; }

    /// <summary>
    /// Launches the gdsfactory export flow. Wired by <see cref="MainViewModel"/>; invoked when
    /// the user, prompted about gdsfactory-native components in a Nazca export, chooses to use
    /// the gdsfactory export instead. Null in headless contexts.
    /// </summary>
    public Func<Task>? RequestGdsFactoryExport { get; set; }

    /// <summary>
    /// Routes a .gds/.gdsii pick from the open-design dialog into the GDS import
    /// flow (the import dialog opens for that file, already analyzed on open).
    /// Wired by <see cref="MainViewModel"/> to
    /// <c>GdsImportButtonViewModel.OpenGdsImportDialogForFileAsync</c>; null in
    /// headless contexts, where the pick surfaces a status hint instead.
    /// </summary>
    public Func<string, Task>? OpenGdsImportRequested { get; set; }

    /// <summary>Initializes a new instance of <see cref="FileOperationsViewModel"/>.</summary>
    public FileOperationsViewModel(
        DesignCanvasViewModel canvas,
        CommandManager commandManager,
        SimpleNazcaExporter nazcaExporter,
        SaxExporter saxExporter,
        ObservableCollection<ComponentTemplate> componentLibrary,
        GdsExportViewModel gdsExport,
        PhotonTorchExportViewModel photonTorchExport,
        VerilogAExportViewModel verilogAExport,
        ErrorConsoleService? errorConsole = null,
        UserSMatrixOverrideStore? userSMatrixOverrideStore = null,
        IUrlLauncher? urlLauncher = null,
        RecentProjectsService? recentProjects = null)
    {
        _canvas = canvas;
        _commandManager = commandManager;
        _nazcaExporter = nazcaExporter;
        _saxExporter = saxExporter;
        _componentLibrary = componentLibrary;
        GdsExport = gdsExport;
        PhotonTorchExport = photonTorchExport;
        VerilogAExport = verilogAExport;
        _errorConsole = errorConsole;
        _userSMatrixOverrideStore = userSMatrixOverrideStore;
        _urlLauncher = urlLauncher ?? PlatformShellLauncher.CreateDefault();
        _recentProjects = recentProjects;

        // Track changes to mark project as unsaved
        _canvas.Components.CollectionChanged += (s, e) => HasUnsavedChanges = true;
        _canvas.Connections.CollectionChanged += (s, e) => HasUnsavedChanges = true;
        // The analysis-output designation (#754) is part of the design file too.
        _canvas.AnalysisOutput.PropertyChanged += (s, e) => HasUnsavedChanges = true;

        // Apply any stored S-matrix override the moment a component lands
        // on the canvas. Without this, the override only takes effect after
        // a Save → Reload cycle — a "did the import even work?" surprise
        // when the user just imported an override on a PDK template and
        // then dragged a fresh instance onto the canvas. The lookup is the
        // same one ApplyAll uses on project load (Identifier-first, then
        // template-key fallback), so existing tests pin the contract.
        _canvas.Components.CollectionChanged += OnComponentsChangedApplyStoredOverrides;
    }

    private void OnComponentsChangedApplyStoredOverrides(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;

        var addedComponents = e.NewItems
            .OfType<ComponentViewModel>()
            .Select(vm => vm.Component)
            .ToList();
        if (addedComponents.Count == 0) return;

        // Precedence per-instance > user-global > template: user-global first, project-local
        // per-instance LAST so they win the last-write-per-wavelength application.
        ApplyUserGlobalOverrides(addedComponents);

        if (StoredSMatrices.Count > 0)
        {
            Services.SMatrixOverrideApplicator.ApplyAll(
                addedComponents,
                StoredSMatrices,
                templateKeyResolver: ResolveTemplateKey,
                geometryKeyResolver: ResolveGeometryKey,
                errorConsole: _errorConsole,
                keyMatchesKnownTemplate: KeyMatchesKnownLibraryTemplate);
        }
    }

    /// <summary>
    /// Applies the user-global PDK template S-matrix overrides. Project-local instance
    /// overrides take precedence and are intentionally applied AFTER these — application is
    /// last-write-wins per wavelength.
    /// </summary>
    private void ApplyUserGlobalOverrides(IEnumerable<Component> components)
    {
        if (_userSMatrixOverrideStore == null ||
            _userSMatrixOverrideStore.Overrides.Count == 0)
            return;

        Services.SMatrixOverrideApplicator.ApplyAll(
            components,
            _userSMatrixOverrideStore.Overrides,
            templateKeyResolver: ResolveTemplateKey,
            errorConsole: _errorConsole,
            keyMatchesKnownTemplate: KeyMatchesKnownLibraryTemplate);
    }

    /// <summary>
    /// Re-applies all user-global PDK template overrides to every live canvas
    /// component. Called by the Component Settings dialog after a successful
    /// import or delete in Per-Template mode so the change propagates to
    /// existing instances without requiring a project reload.
    /// </summary>
    public void ReapplyTemplateOverrides()
    {
        ApplyUserGlobalOverrides(_canvas.Components.Select(vm => vm.Component));
    }

    /// <summary>
    /// Sets the active process this design is locked to (issue #570), e.g. from the
    /// New-Design dialog or a process-picker action.
    /// </summary>
    /// <param name="selection">The process to lock the design to (or Playground).</param>
    /// <param name="markDirty">
    /// False for the startup / New-Design picker: choosing the baseline process of a
    /// pristine empty design is not an unsaved change, and marking it dirty made every
    /// fresh launch answer a spurious "Save changes?" prompt.
    /// </param>
    public void SetActiveProcess(ActiveProcessSelection? selection, bool markDirty = true)
    {
        ActiveProcess = selection;
        if (markDirty)
            HasUnsavedChanges = true;
    }

    [RelayCommand]
    private async Task SaveDesign()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Save not available");
            return;
        }

        var filePath = CurrentFilePath ?? await FileDialogService.ShowSaveFileDialogAsync(
            "Save Design",
            "lun",
            "Lunima Files|*.lun|All Files|*.*");

        if (filePath != null)
        {
            await SaveToFile(filePath);
        }
    }

    [RelayCommand]
    private async Task SaveDesignAs()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Save not available");
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Save Design As",
            "lun",
            "Lunima Files|*.lun|All Files|*.*");

        if (filePath != null)
        {
            await SaveToFile(filePath);
        }
    }

    private async Task SaveToFile(string filePath)
    {
        try
        {
            // Identify which components are groups vs standalone
            var groupComponents = _canvas.Components
                .Where(c => c.Component is ComponentGroup)
                .ToList();
            var childComponentIds = new HashSet<string>();
            foreach (var gc in groupComponents)
            {
                CollectChildIds((ComponentGroup)gc.Component, childComponentIds);
            }

            var componentsList = _canvas.Components.ToList();
            var designData = new DesignFileData
            {
                // Only save non-group, non-child components in the main list
                Components = componentsList
                    .Where(c => c.Component is not ComponentGroup
                                && !childComponentIds.Contains(c.Component.Identifier))
                    .Select(c => CreateComponentData(c))
                    .ToList(),
                Connections = _canvas.Connections.Select(c =>
                {
                    var (startIdx, startPinName) = ResolveConnectionEndpoint(componentsList, c.Connection.StartPin);
                    var (endIdx, endPinName) = ResolveConnectionEndpoint(componentsList, c.Connection.EndPin);
                    return new ConnectionData
                    {
                        StartComponentIndex = startIdx,
                        StartPinName = startPinName,
                        EndComponentIndex = endIdx,
                        EndPinName = endPinName,
                        StartComponentId = startIdx >= 0 ? componentsList[startIdx].Component.Identifier : null,
                        EndComponentId = endIdx >= 0 ? componentsList[endIdx].Component.Identifier : null,
                        CachedSegments = c.Connection.RoutedPath != null
                            ? PathSegmentConverter.ToDtoList(c.Connection.RoutedPath.Segments)
                            : null,
                        IsBlockedFallback = c.Connection.IsBlockedFallback ? true : null,
                        IsInvalidGeometry = c.Connection.RoutedPath?.IsInvalidGeometry == true ? true : null,
                        // Written as an explicit true/false whenever a route exists (never
                        // omitted for false) so a reload can tell "explicitly false" apart
                        // from "file predates this field" — see ConnectionData's doc comment.
                        IsPlaceholderGeometry = c.Connection.RoutedPath?.IsPlaceholderGeometry,
                        IsLocked = c.Connection.IsLocked ? true : null,
                        RoutingStyle = c.Connection.Type != WaveguideType.Auto ? c.Connection.Type.ToString() : null,
                        WidthMicrometers = c.Connection.WidthMicrometers,
                        BendRadiusMicrometers = c.Connection.BendRadiusMicrometers,
                        IsRouteFrozen = c.Connection.IsRouteFrozen ? true : null,
                        BendRadiusOverrides = c.Connection.BendRadiusOverrides.Count > 0
                            ? new Dictionary<int, double>(c.Connection.BendRadiusOverrides)
                            : null,
                        StraightShiftOffsets = c.Connection.StraightShiftOffsets.Count > 0
                            ? new Dictionary<int, double>(c.Connection.StraightShiftOffsets)
                            : null,
                        SourceGdsLayer = c.Connection.SourceGdsLayer,
                        SourceGdsDataType = c.Connection.SourceGdsDataType
                    };
                }).ToList()
            };

            // Serialize groups (including nested groups recursively)
            if (groupComponents.Count > 0)
            {
                designData.Groups = new List<DesignGroupData>();
                foreach (var gc in groupComponents)
                {
                    SerializeGroupRecursively(gc, designData.Groups);
                }
            }

            designData.FormatVersion = CurrentFormatVersion;
            // GDS-imported components travel inside the .lun (issue #830) so the
            // design stays self-contained; null when the design imported nothing.
            // Only sets still referenced by a placed component are embedded — an
            // import whose components were all deleted drops out of the file here.
            var referencedPdkSources = designData.Components.Select(c => c.PdkSource)
                .Concat(designData.Groups?.SelectMany(g => g.ChildComponents.Select(ch => ch.PdkSource))
                        ?? Enumerable.Empty<string?>());
            designData.ImportedGdsComponents = DesignScopedGdsComponents?.CaptureForSave(referencedPdkSources);
            designData.LayerVisibility = LayerVisibility?.CaptureForSave();
            designData.Metadata = BuildMetadataForSave();
            if (StoredSMatrices.Count > 0)
            {
                // Drop overrides orphaned by a parameter/geometry change before persisting:
                // keep only entries still reachable from a placed component (by geometry key
                // or legacy Identifier) plus template-scoped ("::") user-global entries.
                var live = _canvas.Components.Select(vm => vm.Component).ToList();
                var usedGeometryKeys = live.Select(ResolveGeometryKey).ToHashSet();
                var liveIdentifiers = live.Select(c => c.Identifier).ToHashSet();
                var swept = Services.SMatrixOverrideGc.Sweep(StoredSMatrices, usedGeometryKeys, liveIdentifiers);
                if (swept.Count > 0)
                    designData.SMatrices = swept;
            }
            designData.ChipWidthMicrometers  = _canvas.ChipMaxX;
            designData.ChipHeightMicrometers = _canvas.ChipMaxY;
            designData.ActiveProcess = ActiveProcessResolver.ToData(ActiveProcess);
            // Persist the designated analysis-output coupler (#754) by its Identifier —
            // the runtime Component.Id is regenerated on every load.
            designData.AnalysisOutputCoupler = _canvas.AnalysisOutput.CouplerId is Guid outputId
                ? componentsList.FirstOrDefault(c => c.Component.Id == outputId)?.Component.Identifier
                : null;

            var json = JsonSerializer.Serialize(designData, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            await File.WriteAllTextAsync(filePath, json);
            CurrentFilePath = filePath;
            HasUnsavedChanges = false;
            _recentProjects?.RecordProject(filePath);
            UpdateStatus?.Invoke($"Saved to {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to save design: {ex.Message}", ex);
            UpdateStatus?.Invoke($"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a ComponentData DTO from a ComponentViewModel.
    /// Uses FindTemplateName to resolve the correct library template name,
    /// including components ungrouped from UserGroup templates (which have no TemplateName on the VM).
    /// </summary>
    private ComponentData CreateComponentData(ComponentViewModel c)
    {
        return new ComponentData
        {
            TemplateName = FindTemplateName(c.Component),
            PdkSource = c.TemplatePdkSource ?? FindTemplatePdkSource(c.Component),
            X = c.X,
            Y = c.Y,
            Identifier = c.Component.Identifier,
            Rotation = (int)c.Component.Rotation90CounterClock,
            RotationDegrees = c.Component.RotationDegrees,
            SliderValue = c.HasSliders ? c.SliderValue : null,
            SliderValues = SnapshotSliderValues(c.Component),
            LaserWavelengthNm = c.LaserConfig?.WavelengthNm,
            LaserPower = c.LaserConfig?.InputPower,
            LaserEnabled = c.LaserConfig?.IsEnabled == false ? false : null,
            LaserLineShape = c.LaserConfig?.IsSpectralShape == true
                ? c.LaserConfig.LineShape.ToString() : null,
            LaserLinewidthFwhmNm = c.LaserConfig?.LinewidthFwhmNm,
            LaserRinDbPerHz = c.LaserConfig?.RinDbPerHz,
            IsLocked = c.Component.IsLocked ? true : null,
            IsInsertedCrossing = c.Component.IsInsertedCrossing ? true : null,
            HumanReadableName = c.Component.HumanReadableName
        };
    }

    /// <summary>
    /// Captures every slider value keyed by slider number, so multi-parameter
    /// components round-trip all values. Null when the component has no sliders.
    /// </summary>
    private static Dictionary<int, double>? SnapshotSliderValues(CAP_Core.Components.Core.Component component)
    {
        var sliders = component.GetAllSliders();
        if (sliders.Count == 0) return null;
        return sliders.ToDictionary(s => s.Number, s => s.Value);
    }

    /// <summary>
    /// Restores saved slider values onto a component. Prefers the multi-slider map
    ///; falls back to the legacy single value (slider 0) for old files.
    /// </summary>
    private static void RestoreSliderValues(
        CAP_Core.Components.Core.Component component,
        Dictionary<int, double>? sliderValues,
        double? legacySliderValue)
    {
        if (sliderValues != null)
        {
            foreach (var (number, value) in sliderValues)
            {
                var slider = component.GetSlider(number);
                if (slider != null) slider.Value = value;
            }
            return;
        }

        if (legacySliderValue.HasValue)
        {
            var slider = component.GetSlider(0);
            if (slider != null) slider.Value = legacySliderValue.Value;
        }
    }

    /// <summary>
    /// Returns true when the given store key is shaped like a PDK-template-scoped
    /// key (<c>"{pdkSource}::{templateName}"</c>) rather than a per-instance key
    /// (a bare <c>component.Identifier</c> with no <c>::</c> separator). Used during
    /// project load to migrate template-scoped entries to the user-global store.
    /// </summary>
    private static bool IsTemplateScopedKey(string key) => key.Contains("::", StringComparison.Ordinal);

    /// <summary>
    /// Builds the PDK-template-scoped store key (<c>"{pdkSource}::{templateName}"</c>) for a component,
    /// or <c>null</c> when the component has no matching template (e.g. user group). Used as the
    /// fallback lookup in <see cref="Services.SMatrixOverrideApplicator.ApplyAll"/> so PDK-template
    /// overrides reach every instance of the template.
    /// </summary>
    public string? ResolveTemplateKey(Component component)
    {
        var pdkSource = FindTemplatePdkSource(component);
        if (pdkSource == null) return null;
        var templateName = FindTemplateName(component);
        return $"{pdkSource}::{templateName}";
    }

    /// <summary>
    /// Builds the geometry-scoped override-store key for a component, so that an
    /// override imported under a geometry key (FDTD / S-parameter import) re-applies
    /// to every placed instance and copy sharing that geometry. Threads the live
    /// raw-code override (if any) through <see cref="Services.ComponentGeometryKey.For"/>.
    /// </summary>
    private string ResolveGeometryKey(Component component) =>
        CAP.Avalonia.Services.ComponentGeometryKey.For(component);

    /// <summary>
    /// Returns true when the given override-store key (shape
    /// <c>"{pdkSource}::{templateName}"</c>) corresponds to a template that
    /// is currently loaded in the component library — even if no instance of
    /// it is on the canvas right now. Used by
    /// <see cref="Services.SMatrixOverrideApplicator.ApplyAll"/> to
    /// distinguish "deferred override, will apply on placement" from
    /// "truly orphan, the template was renamed or removed". Only the
    /// latter warrants a user-visible warning.
    /// </summary>
    private bool KeyMatchesKnownLibraryTemplate(string key)
    {
        var separatorIdx = key.IndexOf("::", StringComparison.Ordinal);
        if (separatorIdx < 0) return false;
        var pdkSource = key.Substring(0, separatorIdx);
        var templateName = key.Substring(separatorIdx + 2);
        return _componentLibrary.Any(t =>
            t.PdkSource == pdkSource && t.Name == templateName);
    }

    /// <summary>
    /// Finds the PDK source for a component by matching its NazcaFunctionName against the library.
    /// Returns null if no match is found.
    /// </summary>
    private string? FindTemplatePdkSource(Component component) =>
        ComponentPdkSourceResolver.Resolve(component, _componentLibrary);

    /// <summary>
    /// Recursively serializes a ComponentGroup and all its nested child groups.
    /// Adds each group (including nested ones) to the groups list.
    /// </summary>
    private void SerializeGroupRecursively(ComponentViewModel groupVm, List<DesignGroupData> groupsList)
    {
        var group = (ComponentGroup)groupVm.Component;

        // First, recursively serialize any nested child groups
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                // Find the VM for this child group (if it exists on canvas)
                // For nested groups, they won't have their own VM on canvas
                // We'll create a minimal representation
                var childVm = _canvas.Components.FirstOrDefault(c => c.Component == child);
                if (childVm != null)
                {
                    SerializeGroupRecursively(childVm, groupsList);
                }
                else
                {
                    // Nested group - serialize it with its physical position
                    SerializeNestedGroup(childGroup, groupsList);
                }
            }
        }

        // Then serialize this group itself
        var groupDto = ComponentGroupSerializer.ToDto(group);
        var childDataList = new List<ChildComponentData>();
        CollectChildComponentData(group, childDataList);

        groupsList.Add(new DesignGroupData
        {
            GroupDto = groupDto,
            ChildComponents = childDataList,
            CanvasX = groupVm.X,
            CanvasY = groupVm.Y
        });
    }

    /// <summary>
    /// Serializes a nested ComponentGroup that doesn't have its own canvas VM.
    /// </summary>
    private void SerializeNestedGroup(ComponentGroup group, List<DesignGroupData> groupsList)
    {
        // First, recursively serialize any nested child groups
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                SerializeNestedGroup(childGroup, groupsList);
            }
        }

        // Then serialize this group
        var groupDto = ComponentGroupSerializer.ToDto(group);
        var childDataList = new List<ChildComponentData>();
        CollectChildComponentData(group, childDataList);

        groupsList.Add(new DesignGroupData
        {
            GroupDto = groupDto,
            ChildComponents = childDataList,
            CanvasX = group.PhysicalX,
            CanvasY = group.PhysicalY
        });
    }

    /// <summary>
    /// Collects child component data (with template names) from a group.
    /// Only collects direct children that are NOT ComponentGroups (nested groups are serialized separately).
    /// </summary>
    private void CollectChildComponentData(
        ComponentGroup group, List<ChildComponentData> childDataList)
    {
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup)
            {
                // Skip nested groups - they have their own DesignGroupData entry
                continue;
            }

            var templateName = FindTemplateName(child);
            var pdkSource = FindTemplatePdkSource(child);

            childDataList.Add(new ChildComponentData
            {
                Identifier = child.Identifier,
                ComponentGuid = child.Id.ToString(),
                TemplateName = templateName,
                PdkSource = pdkSource,
                X = child.PhysicalX,
                Y = child.PhysicalY,
                Rotation = (int)child.Rotation90CounterClock,
                RotationDegrees = child.RotationDegrees,
                SliderValue = child.GetAllSliders().Count > 0
                    ? child.GetSlider(0)?.Value : null,
                SliderValues = SnapshotSliderValues(child),
                IsLocked = child.IsLocked ? true : null,
                HumanReadableName = child.HumanReadableName
            });
        }
    }

    /// <summary>
    /// Finds the template name for a component by checking the canvas VMs
    /// and falling back to matching against the component library by NazcaFunctionName.
    /// </summary>
    /// <summary>
    /// Builds the DesignMetadata for the current save, preserving the Created date from the
    /// last loaded file so that re-saving does not reset the original creation timestamp.
    /// </summary>
    private DesignMetadata BuildMetadataForSave()
    {
        var now = DateTime.UtcNow;
        var createdDate = _loadedMetadata?.Authorship?.Created
            ?? now.ToString("yyyy-MM-dd");

        return new DesignMetadata
        {
            PdkVersions = _loadedMetadata?.PdkVersions ?? new Dictionary<string, string>(),
            DesignRules = _loadedMetadata?.DesignRules,
            Description = _loadedMetadata?.Description,
            Authorship = new AuthorshipData
            {
                Created = createdDate,
                Modified = now.ToString("o"),
                Author = _loadedMetadata?.Authorship?.Author,
                Version = _loadedMetadata?.Authorship?.Version
            }
        };
    }

    private string FindTemplateName(Component component)
    {
        // Check if the component has a VM on the canvas with a template name
        var vm = _canvas.Components.FirstOrDefault(c => c.Component == component);
        if (vm?.TemplateName != null)
            return vm.TemplateName;

        // Match by NazcaFunctionName against the component library
        var nazcaFunc = component.NazcaFunctionName;
        if (!string.IsNullOrEmpty(nazcaFunc))
        {
            var match = _componentLibrary.FirstOrDefault(t =>
            {
                var templateFunc = t.NazcaFunctionName
                    ?? $"nazca_{t.Name.ToLower().Replace(" ", "_")}";
                return templateFunc == nazcaFunc;
            });
            if (match != null)
                return match.Name;
        }

        // Last resort: use identifier
        return component.Identifier;
    }

    /// <summary>
    /// Recursively collects all child component identifiers from a group.
    /// </summary>
    private static void CollectChildIds(ComponentGroup group, HashSet<string> ids)
    {
        foreach (var child in group.ChildComponents)
        {
            ids.Add(child.Identifier);
            if (child is ComponentGroup nested)
            {
                CollectChildIds(nested, ids);
            }
        }
    }

    /// <summary>
    /// Resolves which canvas component and pin name to use when serializing a connection endpoint.
    /// Handles both regular components (direct match) and group external pins (via InternalPin lookup).
    /// </summary>
    /// <param name="components">All top-level components on the canvas.</param>
    /// <param name="pin">The physical pin on the connection endpoint.</param>
    /// <returns>The component index and pin name to store in ConnectionData.</returns>
    internal static (int index, string pinName) ResolveConnectionEndpoint(
        List<ComponentViewModel> components, PhysicalPin pin)
    {
        // Direct match: pin belongs to a top-level canvas component
        int directIndex = components.FindIndex(c => c.Component == pin.ParentComponent);
        if (directIndex >= 0)
            return (directIndex, pin.Name);

        // Group match: pin is the InternalPin of a group's external pin
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i].Component is ComponentGroup group)
            {
                var match = group.ExternalPins.FirstOrDefault(ep => ep.InternalPin == pin);
                if (match != null)
                    return (i, match.Name);
            }
        }

        return (-1, pin.Name);
    }

    /// <summary>
    /// Resolves the physical pin to connect to on a component during load.
    /// Handles both regular components (PhysicalPins lookup) and groups (ExternalPins lookup via external pin name).
    /// </summary>
    /// <param name="component">The component to find the pin on.</param>
    /// <param name="pinName">The pin name stored in ConnectionData.</param>
    /// <returns>The physical pin, or null if not found.</returns>
    internal static PhysicalPin? ResolvePin(Component component, string pinName)
    {
        // For regular components: look up by physical pin name directly
        var directPin = component.PhysicalPins.FirstOrDefault(p => p.Name == pinName);
        if (directPin != null)
            return directPin;

        // For groups: look up by external pin name and return its InternalPin
        if (component is ComponentGroup group)
            return group.ExternalPins.FirstOrDefault(ep => ep.Name == pinName)?.InternalPin;

        return null;
    }

    [RelayCommand]
    private async Task LoadDesign()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Load not available");
            return;
        }

        if (!await ConfirmUnsavedChangesAsync(LoadPromptMessage))
            return;

        var filePath = await FileDialogService.ShowOpenFileDialogAsync(
            "Load Design",
            "Lunima Files|*.lun|GDS files (*.gds;*.gdsii)|*.gds;*.gdsii|All Files|*.*");

        if (filePath == null)
            return;

        // A GDS pick routes into the GDS import flow instead of the .lun load
        // path: the import dialog opens for that file and analyzes it on open.
        // Like a .lun load, this REPLACES the current design — the clear
        // happens inside OpenGdsImportAsync, covered by the (already answered)
        // unsaved-changes prompt: prompt → pick → clear → import.
        if (IsGdsFile(filePath))
        {
            await OpenGdsImportAsync(filePath);
            return;
        }

        await LoadDesignFromFileAsync(filePath);
    }

    /// <summary>True for GDS II layout files (.gds/.gdsii, case-insensitive).</summary>
    internal static bool IsGdsFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".gds", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".gdsii", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hands a GDS pick from the open-design dialog to the import flow and fires
    /// <see cref="ProjectOpened"/> so the Home screen lets go of the window — the
    /// import dialog and its canvas result must not stay hidden behind it.
    /// Loading a file starts fresh, so the current design is cleared FIRST — the
    /// same reset the .lun load performs (<see cref="ClearCanvas"/> plus the
    /// load-time migration state <see cref="LoadDesignFromFileAsync"/> resets);
    /// the import flow itself only ADDS to the canvas and would otherwise merge
    /// into the existing content. The discard was covered by the unsaved-changes
    /// prompt before the pick. The clear marks the project dirty (canvas change
    /// tracking) and detaches it from the previous project file — a later Save
    /// must not overwrite a .lun the imported design did not come from.
    /// </summary>
    private async Task OpenGdsImportAsync(string gdsPath)
    {
        if (OpenGdsImportRequested is null)
        {
            UpdateStatus?.Invoke(Services.Localization.LocalizationService.Instance
                .Translate("GdsImport.StatusUnavailable"));
            return;
        }

        ClearCanvas();
        _pinCalibrationMigratedComponents.Clear();
        CurrentFilePath = null;
        _loadedMetadata = null;

        await OpenGdsImportRequested(gdsPath);
        ProjectOpened?.Invoke();
    }

    /// <summary>
    /// Loads a design directly from a file path (no file picker), prompting to save
    /// unsaved changes first. Used by the Home screen's recent-projects list and
    /// command-line file arguments.
    /// </summary>
    /// <param name="filePath">Absolute path to the .lun file to open.</param>
    /// <returns>True when the design was loaded; false when cancelled, missing, or failed.</returns>
    public async Task<bool> LoadDesignFromPathAsync(string filePath)
    {
        if (!await ConfirmUnsavedChangesAsync(LoadPromptMessage))
            return false;

        return await LoadDesignFromFileAsync(filePath);
    }

    /// <summary>
    /// Opens a design as an untitled copy, detached from its source file — used
    /// for the Home screen's shipped examples. The design loads, but the file
    /// path stays null (Save prompts for a new location, so the source can't be
    /// overwritten), the design is marked unsaved, and the source is not
    /// recorded in the recent-projects list.
    /// </summary>
    /// <param name="filePath">Absolute path to the template/example .lun file.</param>
    /// <returns>True when the design was opened; false when cancelled, missing, or failed.</returns>
    public async Task<bool> OpenDesignAsCopyAsync(string filePath)
    {
        if (!await ConfirmUnsavedChangesAsync(LoadPromptMessage))
            return false;

        if (!await LoadDesignFromFileAsync(filePath, recordRecent: false))
            return false;

        CurrentFilePath = null;
        // A copy must not inherit the source's metadata (Created date, author);
        // BuildMetadataForSave then stamps fresh values on the first save.
        _loadedMetadata = null;
        HasUnsavedChanges = true;
        return true;
    }

    /// <summary>
    /// Core load step shared by the file-picker command and path-based loading:
    /// reads a .lun file and replaces the current canvas contents with it.
    /// Does NOT prompt about unsaved changes — callers do that first.
    /// </summary>
    private async Task<bool> LoadDesignFromFileAsync(string filePath, bool recordRecent = true)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var designData = JsonSerializer.Deserialize<DesignFileData>(json);

                if (designData == null)
                {
                    UpdateStatus?.Invoke("Invalid design file");
                    return false;
                }

                if (designData.FormatVersion != CurrentFormatVersion)
                {
                    var actual = string.IsNullOrEmpty(designData.FormatVersion) ? "<missing>" : designData.FormatVersion;
                    _errorConsole?.LogWarning(
                        $"Legacy .lun file detected (FormatVersion: {actual}). Loading with missing PIR sections (S-matrices, metadata, simulation results) left empty. File will be upgraded to {CurrentFormatVersion} on next save.");
                }

                // Clear current design
                _canvas.Components.Clear();
                _canvas.Connections.Clear();
                _canvas.AllPins.Clear();
                _canvas.ConnectionManager.Clear();
                _commandManager.ClearHistory();
                _pinCalibrationMigratedComponents.Clear();

                // Design-scoped imported components (#830): restore the sets embedded
                // in this .lun (replacing the previous design's) and migrate any legacy
                // global "GDS Import - *" PDKs the file still references — BOTH must
                // happen before the placements below resolve their templates.
                var migratedGdsSets = 0;
                if (DesignScopedGdsComponents != null)
                {
                    DesignScopedGdsComponents.RestoreDesignScope(
                        designData.ImportedGdsComponents, w => _errorConsole?.LogWarning(w));
                    var referencedPdkSources = designData.Components.Select(c => c.PdkSource)
                        .Concat(designData.Groups?.SelectMany(g => g.ChildComponents.Select(ch => ch.PdkSource))
                                ?? Enumerable.Empty<string?>())
                        .OfType<string>();
                    migratedGdsSets = DesignScopedGdsComponents.MigrateLegacyImportPdks(
                        referencedPdkSources, w => _errorConsole?.LogWarning(w));
                }

                // Per-layer visibility overrides for imported geometry (#858).
                LayerVisibility?.Restore(designData.LayerVisibility);

                // Load standalone components
                foreach (var compData in designData.Components)
                {
                    LoadComponentFromData(compData);
                }

                // Load ComponentGroups
                var groupCount = 0;
                if (designData.Groups != null)
                {
                    groupCount = LoadGroups(designData.Groups);
                }

                // Load connections (index-based references to _canvas.Components)
                foreach (var connData in designData.Connections)
                {
                    LoadConnectionFromData(connData);
                }

                // Pin-calibration migration: report discarded stale routes and re-route them.
                ReportPinCalibrationMigrations();

                // Rebuild dissolution records for loaded auto-inserted crossings (#705)
                // so they dissolve/re-evaluate exactly like ones inserted this session.
                RebuildCrossingRecords();

                // Notify all connections about their paths for UI rendering
                foreach (var conn in _canvas.Connections)
                {
                    conn.NotifyPathChanged();
                }

                // Restore chip size if saved. The two fields are written together by Save(), so
                // a half-present pair indicates a truncated/edited file — warn the user via the
                // error console rather than silently applying half the chip size.
                bool hasWidth  = designData.ChipWidthMicrometers.HasValue;
                bool hasHeight = designData.ChipHeightMicrometers.HasValue;
                if (hasWidth && hasHeight)
                {
                    ApplyChipSizeAfterLoad?.Invoke(
                        designData.ChipWidthMicrometers!.Value,
                        designData.ChipHeightMicrometers!.Value);
                }
                else if (hasWidth || hasHeight)
                {
                    _errorConsole?.LogWarning(
                        $"File '{Path.GetFileName(filePath)}' has only one chip-size field set " +
                        $"(width: {hasWidth}, height: {hasHeight}). Falling back to current canvas size.");
                }

                // Restore the designated analysis-output coupler (#754); files without
                // the field (older versions) simply load with no designation.
                RestoreAnalysisOutput(designData);

                // Preserve PIR metadata so Created date survives subsequent saves
                _loadedMetadata = designData.Metadata;

                // Restore the active process (issue #570), or infer one for legacy files
                // that predate single-process support from the placed components' PDKs.
                var storedProcess = ActiveProcessResolver.FromData(designData.ActiveProcess);
                if (storedProcess != null)
                {
                    // Re-anchor the stored snapshot to the installed catalog: compatible
                    // PDKs installed since the save join the process, and a design whose
                    // PDKs are missing warns instead of silently bricking the library.
                    var installedCatalog = ProcessCatalogProvider?.Invoke() ?? Array.Empty<ProcessGroup>();
                    ActiveProcess = ActiveProcessResolver.Revalidate(
                        storedProcess, installedCatalog, out var revalidationWarning);
                    if (revalidationWarning != null)
                        OnProcessMigrationWarning?.Invoke(revalidationWarning);
                }
                else
                {
                    var catalog = ProcessCatalogProvider?.Invoke() ?? Array.Empty<ProcessGroup>();
                    var pdkSources = designData.Components.Select(c => c.PdkSource)
                        .Concat(designData.Groups?.SelectMany(g => g.ChildComponents.Select(ch => ch.PdkSource))
                                ?? Enumerable.Empty<string?>());
                    ActiveProcess = ActiveProcessResolver.Migrate(pdkSources, catalog, out var warning,
                        ProcessAgnosticPdkNamesProvider?.Invoke() ?? System.Array.Empty<string>());
                    if (warning != null) OnProcessMigrationWarning?.Invoke(warning);
                }

                // Restore imported S-matrices from PIR section
                StoredSMatrices.Clear();
                if (designData.SMatrices != null)
                {
                    int migratedCount = 0;
                    foreach (var kv in designData.SMatrices)
                    {
                        // Migration: PDK-template-scoped keys ("{pdkSource}::{templateName}")
                        // used to live in the project file. They now belong to the user-global
                        // store so the override applies to every project the user opens.
                        // Move them out so a subsequent save writes a clean project file.
                        if (_userSMatrixOverrideStore != null && IsTemplateScopedKey(kv.Key))
                        {
                            _userSMatrixOverrideStore.Overrides[kv.Key] = kv.Value;
                            migratedCount++;
                        }
                        else
                        {
                            StoredSMatrices[kv.Key] = kv.Value;
                        }
                    }

                    if (migratedCount > 0)
                    {
                        _userSMatrixOverrideStore!.Save();
                        _errorConsole?.LogWarning(
                            $"Migrated {migratedCount} PDK template S-matrix override(s) from project file to user-global storage. " +
                            "These now apply to all projects. Save this project to finalise the migration.");
                        HasUnsavedChanges = true;
                    }

                    // Precedence per-instance > user-global > template: user-global first,
                    // project-local per-instance LAST (last-write-wins per wavelength).
                    var allComponents = _canvas.Components.Select(vm => vm.Component).ToList();
                    ApplyUserGlobalOverrides(allComponents);
                    // Project load is the one place we hold the COMPLETE component set,
                    // so it is also the only place an orphan check is meaningful — let
                    // it surface genuinely unmatched overrides (renamed/removed).
                    Services.SMatrixOverrideApplicator.ApplyAll(
                        allComponents,
                        StoredSMatrices,
                        templateKeyResolver: ResolveTemplateKey,
                        geometryKeyResolver: ResolveGeometryKey,
                        errorConsole: _errorConsole,
                        keyMatchesKnownTemplate: KeyMatchesKnownLibraryTemplate,
                        reportOrphans: true);
                }
                else
                {
                    // No project overrides — still apply user-global ones so the
                    // user's PDK template edits show up in projects that never
                    // had any project-scoped overrides of their own.
                    ApplyUserGlobalOverrides(_canvas.Components.Select(vm => vm.Component));
                }

                CurrentFilePath = filePath;
                HasUnsavedChanges = false;
                if (migratedGdsSets > 0)
                {
                    // The design must be re-saved to embed the migrated components —
                    // until then it still depends on the legacy user-PDK files.
                    _errorConsole?.LogWarning(
                        $"Migrated {migratedGdsSets} legacy imported GDS component set(s) into this design. " +
                        "Save the design to embed them in the .lun file.");
                    HasUnsavedChanges = true;
                }
                if (recordRecent)
                {
                    _recentProjects?.RecordProject(filePath);
                }
                UpdateStatus?.Invoke($"Loaded {Path.GetFileName(filePath)} ({_canvas.Components.Count} components, {_canvas.Connections.Count} connections, {groupCount} groups)");
                _commandManager.NotifyStateChanged();

                // Rebuild hierarchy tree after loading
                RebuildHierarchy?.Invoke();

                // Auto zoom-to-fit after loading
                ZoomToFitAfterLoad?.Invoke(900, 800);

                ProjectOpened?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                _errorConsole?.LogError($"Failed to load design: {ex.Message}", ex);
                UpdateStatus?.Invoke($"Load failed: {ex.Message}");
                return false;
            }
        }

        UpdateStatus?.Invoke($"File not found: {filePath}");
        return false;
    }

    /// <summary>
    /// Prompts to save unsaved changes (Save / Don't Save / Cancel) before a
    /// destructive action. Returns true when it is safe to proceed: nothing was
    /// unsaved, the user saved successfully, or chose Don't Save. Returns false
    /// on Cancel or when the user aborted the save dialog.
    /// </summary>
    private async Task<bool> ConfirmUnsavedChangesAsync(string message)
    {
        if (!HasUnsavedChanges || MessageBoxService == null)
            return true;

        var result = await MessageBoxService.ShowSavePromptAsync(message, "Save Changes?");
        if (result == SavePromptResult.Save)
        {
            await SaveDesign();

            // Still dirty means the user cancelled the save dialog — abort the action.
            return !HasUnsavedChanges;
        }

        return result == SavePromptResult.DontSave;
    }

    /// <summary>
    /// Asks whether the application may close, prompting to save unsaved changes
    /// first. Wired to the main window's Closing event.
    /// </summary>
    /// <returns>True when closing may proceed; false when the user cancelled.</returns>
    public async Task<bool> ConfirmCloseAsync()
    {
        return await ConfirmUnsavedChangesAsync("Do you want to save your changes before closing?");
    }

    /// <summary>
    /// Creates a new empty project, prompting to save if there are unsaved changes.
    /// Exits group edit mode if active before clearing the canvas.
    /// </summary>
    [RelayCommand]
    private async Task NewProject() => await TryNewProjectAsync();

    /// <summary>
    /// Command body of File → New, returning whether the new project was actually
    /// created. False means the user cancelled (save prompt or save dialog) and the
    /// current design is untouched — callers such as the process picker must NOT
    /// proceed in that case. An empty canvas is no substitute for this signal: the
    /// canvas can be empty while the operation was still cancelled.
    /// </summary>
    public async Task<bool> TryNewProjectAsync()
    {
        if (!await ConfirmUnsavedChangesAsync(
                "Do you want to save your changes before creating a new project?"))
            return false;

        // Exit group edit mode if active
        if (_canvas.IsInGroupEditMode)
        {
            _canvas.ExitToRoot();
        }

        // Clear the canvas
        ClearCanvas();

        CurrentFilePath = null;
        _loadedMetadata = null;
        HasUnsavedChanges = false;
        UpdateStatus?.Invoke("New project created");

        // Rebuild hierarchy
        RebuildHierarchy?.Invoke();

        ProjectOpened?.Invoke();
        return true;
    }

    /// <summary>
    /// Clears all components and connections from the canvas.
    /// Also clears the per-component S-matrix override store: without this,
    /// File → New (which calls ClearCanvas) leaves overrides from the
    /// previous design behind. A subsequent Save would write them as
    /// orphan entries (no matching component by Identifier or template
    /// key) into the new file — the user gets warnings on next Load and
    /// state from the prior design leaks into a "fresh" project.
    /// </summary>
    private void ClearCanvas()
    {
        _canvas.Components.Clear();
        _canvas.Connections.Clear();
        _canvas.AllPins.Clear();
        _canvas.ConnectionManager.Clear();
        _canvas.AnalysisOutput.Clear();
        _commandManager.ClearHistory();
        StoredSMatrices.Clear();
        // Imported GDS components are design-scoped (#830): a fresh project must
        // not inherit the previous design's imported library entries.
        DesignScopedGdsComponents?.ClearDesignScope();
        // Layer-visibility overrides are per design (#858): reset to all-visible.
        LayerVisibility?.ClearForNewDesign();
    }

    /// <summary>
    /// Restores the analysis-output designation (#754) from a loaded design file,
    /// re-anchoring the stored component Identifier to the freshly created component's
    /// runtime id. A stale reference (component renamed/removed outside the app) is
    /// cleared with a warning instead of silently pinning a wrong output.
    /// </summary>
    private void RestoreAnalysisOutput(DesignFileData designData)
    {
        if (designData.AnalysisOutputCoupler == null)
        {
            _canvas.AnalysisOutput.Clear();
            return;
        }

        var coupler = _canvas.Components
            .FirstOrDefault(c => c.Component.Identifier == designData.AnalysisOutputCoupler);
        if (coupler != null)
        {
            _canvas.AnalysisOutput.Designate(coupler.Component.Id);
            return;
        }

        _canvas.AnalysisOutput.Clear();
        _errorConsole?.LogWarning(
            $"Designated analysis output '{designData.AnalysisOutputCoupler}' was not found in the design — designation cleared.");
    }

    /// <summary>
    /// Finds a template by name and optional PDK source.
    /// When PdkSource is provided, prefers an exact match; falls back to name-only for old files.
    /// </summary>
    private ComponentTemplate? FindTemplate(string templateName, string? pdkSource)
    {
        if (!string.IsNullOrEmpty(pdkSource))
        {
            var exact = _componentLibrary.FirstOrDefault(t =>
                t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase)
                && t.PdkSource.Equals(pdkSource, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        return _componentLibrary.FirstOrDefault(t =>
            t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads a single component from saved data and adds it to the canvas.
    /// </summary>
    private ComponentViewModel? LoadComponentFromData(ComponentData compData)
    {
        var template = FindTemplate(compData.TemplateName, compData.PdkSource);

        if (template == null)
            return null;

        var component = ComponentTemplates.CreateFromTemplate(template, compData.X, compData.Y);

        // Restore identifier to preserve references
        component.Identifier = compData.Identifier;

        // Restore HumanReadableName
        if (compData.HumanReadableName != null)
            component.HumanReadableName = compData.HumanReadableName;

        // Apply rotation: exact continuous angle when the file carries one
        // (GDS imports keep non-cardinal rotations); cardinal angles keep the
        // legacy quarter-turn loop (discrete-rotation sync, numerically exact).
        if (compData.RotationDegrees is double exactDegrees)
        {
            int quarterTurns = (int)Math.Round(exactDegrees / 90.0);
            if (Math.Abs(exactDegrees - (quarterTurns * 90.0)) < 1e-6)
            {
                for (int i = 0; i < ((quarterTurns % 4) + 4) % 4; i++)
                {
                    ApplyRotationToComponent(component);
                }
            }
            else
            {
                RotateComponentCommand.ApplyModelRotation(component, exactDegrees);
            }
        }
        else
        {
            for (int i = 0; i < compData.Rotation; i++)
            {
                ApplyRotationToComponent(component);
            }
        }

        var vm = _canvas.AddComponent(component, template.Name, template.PdkSource);

        // Restore slider values (all sliders; legacy single value as fallback)
        RestoreSliderValues(vm.Component, compData.SliderValues, compData.SliderValue);

        // Restore laser configuration
        if (vm.LaserConfig != null)
        {
            if (compData.LaserWavelengthNm.HasValue)
                vm.LaserConfig.WavelengthNm = compData.LaserWavelengthNm.Value;
            if (compData.LaserPower.HasValue)
                vm.LaserConfig.InputPower = compData.LaserPower.Value;
            if (compData.LaserEnabled.HasValue)
                vm.LaserConfig.IsEnabled = compData.LaserEnabled.Value;
            if (compData.LaserLineShape != null
                && Enum.TryParse<CAP_Core.ExternalPorts.LaserSpectrum.LaserLineShape>(
                    compData.LaserLineShape, out var lineShape))
                vm.LaserConfig.LineShape = lineShape;
            if (compData.LaserLinewidthFwhmNm.HasValue)
                vm.LaserConfig.LinewidthFwhmNm = compData.LaserLinewidthFwhmNm.Value;
            if (compData.LaserRinDbPerHz.HasValue)
                vm.LaserConfig.RinDbPerHz = compData.LaserRinDbPerHz.Value;
        }

        // Restore lock state
        if (compData.IsLocked == true)
            component.IsLocked = true;

        // Restore the auto-inserted-crossing marker so its dissolution record
        // can be rebuilt once all connections are loaded (#705)
        if (compData.IsInsertedCrossing == true)
            component.IsInsertedCrossing = true;

        return vm;
    }

    /// <summary>
    /// Loads ComponentGroups from saved design data, handling nested groups correctly.
    /// Creates child components first, then reconstructs groups in dependency order.
    /// </summary>
    private int LoadGroups(List<DesignGroupData> groupDataList)
    {
        // Primary lookup: by saved Guid (prevents name-collision bugs when copying groups).
        // Fallback lookup: by Identifier string (for old files that predate Guid fields).
        var guidLookup = new Dictionary<Guid, Component>();
        var nameFallback = new Dictionary<string, Component>();

        // First pass: Create all non-group child components
        foreach (var groupData in groupDataList)
        {
            foreach (var childData in groupData.ChildComponents)
            {
                // Determine the lookup key for this child
                var hasGuid = childData.ComponentGuid != null
                              && Guid.TryParse(childData.ComponentGuid, out var childGuid);

                // Skip if already created under the same key
                if (hasGuid && guidLookup.ContainsKey(Guid.Parse(childData.ComponentGuid!)))
                    continue;
                if (!hasGuid && nameFallback.ContainsKey(childData.Identifier))
                    continue;

                var template = FindTemplate(childData.TemplateName, childData.PdkSource);

                if (template == null)
                    continue;

                var child = ComponentTemplates.CreateFromTemplate(
                    template, childData.X, childData.Y);

                // Restore human-readable name
                child.Identifier = childData.Identifier;

                // Restore HumanReadableName
                if (childData.HumanReadableName != null)
                    child.HumanReadableName = childData.HumanReadableName;

                // Apply rotation: the exact continuous angle when the file
                // carries one (GDS imports keep non-cardinal rotations; the
                // exact path also records the unrotated dims for outline
                // rendering). Cardinal angles keep the legacy quarter-turn
                // loop — it keeps the discrete rotation enum in sync and is
                // numerically exact (no trig noise).
                if (childData.RotationDegrees is double exactDegrees)
                {
                    int quarterTurns = (int)Math.Round(exactDegrees / 90.0);
                    if (Math.Abs(exactDegrees - (quarterTurns * 90.0)) < 1e-6)
                    {
                        for (int i = 0; i < ((quarterTurns % 4) + 4) % 4; i++)
                        {
                            ApplyRotationToComponent(child);
                        }
                    }
                    else
                    {
                        RotateComponentCommand.ApplyModelRotation(child, exactDegrees);
                    }
                }
                else
                {
                    for (int i = 0; i < childData.Rotation; i++)
                    {
                        ApplyRotationToComponent(child);
                    }
                }

                // Restore slider values (all sliders; legacy single value as fallback)
                RestoreSliderValues(child, childData.SliderValues, childData.SliderValue);

                if (childData.IsLocked == true)
                    child.IsLocked = true;

                // Index by saved Guid (primary) and by name (fallback for old files)
                if (hasGuid)
                    guidLookup[Guid.Parse(childData.ComponentGuid!)] = child;
                nameFallback[child.Identifier] = child;
            }
        }

        // Second pass: Reconstruct groups in dependency order (children before parents)
        var orderedGroups = TopologicalSortGroups(groupDataList);

        foreach (var groupData in orderedGroups)
        {
            // Reconstruct the group using Guid-based lookup with name fallback
            var group = ComponentGroupSerializer.FromDto(
                groupData.GroupDto, guidLookup, nameFallback);

            // Index the group itself so nested parents can find it
            if (groupData.GroupDto.IdGuid != null
                && Guid.TryParse(groupData.GroupDto.IdGuid, out var groupGuid))
            {
                guidLookup[groupGuid] = group;
            }
            nameFallback[group.Identifier] = group;

            // Only add top-level groups (groups without a parent) to the canvas
            if (groupData.GroupDto.ParentGroupId == null)
            {
                var groupVm = _canvas.AddComponent(group);
                groupVm.X = groupData.CanvasX;
                groupVm.Y = groupData.CanvasY;
                group.PhysicalX = groupData.CanvasX;
                group.PhysicalY = groupData.CanvasY;
            }
        }

        return orderedGroups.Count;
    }

    /// <summary>
    /// Sorts groups in topological order so that child groups are loaded before their parents.
    /// This ensures that when we reconstruct a parent group, all its child groups are already available.
    /// </summary>
    private List<DesignGroupData> TopologicalSortGroups(List<DesignGroupData> groupDataList)
    {
        // Build dependency map: group ID -> list of group IDs that depend on it (parents)
        var dependents = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var groupData in groupDataList)
        {
            var groupId = groupData.GroupDto.Identifier;
            if (!inDegree.ContainsKey(groupId))
                inDegree[groupId] = 0;

            // Count how many child groups this group has (determines loading order)
            foreach (var childId in groupData.GroupDto.ChildComponentIds)
            {
                // Check if this child is a group (appears as a group in the list)
                var childGroup = groupDataList.FirstOrDefault(g => g.GroupDto.Identifier == childId);
                if (childGroup != null)
                {
                    // This group depends on its child group being loaded first
                    if (!dependents.ContainsKey(childId))
                        dependents[childId] = new List<string>();
                    dependents[childId].Add(groupId);
                    inDegree[groupId]++;
                }
            }
        }

        // Kahn's algorithm for topological sort
        var queue = new Queue<string>();
        foreach (var groupData in groupDataList)
        {
            if (inDegree[groupData.GroupDto.Identifier] == 0)
                queue.Enqueue(groupData.GroupDto.Identifier);
        }

        var sorted = new List<DesignGroupData>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var groupData = groupDataList.First(g => g.GroupDto.Identifier == currentId);
            sorted.Add(groupData);

            if (dependents.ContainsKey(currentId))
            {
                foreach (var dependentId in dependents[currentId])
                {
                    inDegree[dependentId]--;
                    if (inDegree[dependentId] == 0)
                        queue.Enqueue(dependentId);
                }
            }
        }

        // If we couldn't sort all groups, there's a cycle (shouldn't happen)
        // Just return the original order as fallback
        return sorted.Count == groupDataList.Count ? sorted : groupDataList;
    }

    /// <summary>
    /// Finds a canvas component by identifier string (preferred) or by index (fallback for old files).
    /// Returns null if the component cannot be found.
    /// </summary>
    private ComponentViewModel? ResolveComponentForLoad(string? componentId, int fallbackIndex)
    {
        if (!string.IsNullOrEmpty(componentId))
            return _canvas.Components.FirstOrDefault(c => c.Component.Identifier == componentId);

        if (fallbackIndex >= 0 && fallbackIndex < _canvas.Components.Count)
            return _canvas.Components[fallbackIndex];

        return null;
    }

    /// <summary>
    /// Rebuilds crossing dissolution records for auto-inserted crossings restored
    /// from a saved design (#705). No-op when the crossing feature is disabled;
    /// re-enabling it later triggers the same rebuild via the canvas binder.
    /// </summary>
    private void RebuildCrossingRecords()
    {
        var crossing = _canvas.ConnectionManager.CrossingInsertion;
        if (crossing == null)
            return;

        CAP_Core.Routing.CrossingInsertion.CrossingRecordRebuilder.Rebuild(
            crossing,
            _canvas.ConnectionManager,
            _canvas.Components.Select(vm => vm.Component));
    }

    /// <summary>
    /// Loads a single connection from saved data.
    /// Prefers identifier-based lookup (StartComponentId/EndComponentId) over index-based
    /// to correctly handle mixed standalone+group designs where load order differs from save order.
    /// Falls back to index-based for old files that predate the identifier fields.
    /// </summary>
    private void LoadConnectionFromData(ConnectionData connData)
    {
        var startComp = ResolveComponentForLoad(connData.StartComponentId, connData.StartComponentIndex);
        var endComp = ResolveComponentForLoad(connData.EndComponentId, connData.EndComponentIndex);

        if (startComp == null || endComp == null)
            return;

        var startPin = ResolvePin(startComp.Component, connData.StartPinName);
        var endPin = ResolvePin(endComp.Component, connData.EndPinName);

        if (startPin == null || endPin == null)
            return;

        var cachedPath = PathSegmentConverter.ToRoutedPath(
            connData.CachedSegments, connData.IsBlockedFallback ?? false,
            connData.IsInvalidGeometry ?? false, connData.IsPlaceholderGeometry);

        // Pin-calibration migration (round-5 review [2]): when a PDK release corrected a
        // component's pin ANGLES (positions unchanged), the saved geometry still touches
        // the pins, so the incremental router would keep it — visibly docking against the
        // port direction and kinking into the component on GDS export. Discard the stale
        // geometry (the post-load pass re-routes) and drop the frozen state with it.
        bool pinCalibrationChanged = false;
        if (cachedPath != null && cachedPath.IsValid)
        {
            var (startOk, endOk) = CachedRouteValidator.CheckPinDirections(startPin, endPin, cachedPath);
            if (!startOk) _pinCalibrationMigratedComponents.Add(startComp.Component.Name);
            if (!endOk) _pinCalibrationMigratedComponents.Add(endComp.Component.Name);
            pinCalibrationChanged = !startOk || !endOk;
            if (pinCalibrationChanged) cachedPath = null;
        }

        WaveguideConnectionViewModel? connVm;

        if (cachedPath != null && cachedPath.IsValid)
        {
            connVm = _canvas.ConnectPinsWithCachedRoute(startPin, endPin, cachedPath);
        }
        else
        {
            connVm = _canvas.ConnectPins(startPin, endPin);
        }

        // Restore lock state
        if (connVm != null && connData.IsLocked == true)
        {
            connVm.Connection.IsLocked = true;
        }

        // Restore the import source-layer tag (route-derived GDS connections); null in
        // files that predate the field — the connection stays untagged, unchanged.
        if (connVm != null)
        {
            connVm.Connection.SourceGdsLayer = connData.SourceGdsLayer;
            connVm.Connection.SourceGdsDataType = connData.SourceGdsDataType;
        }

        // Restore routing style / interconnect settings / freeze state (issue #574)
        if (connVm != null)
            RestoreRoutingSettings(connVm.Connection, connData, keepFrozenGeometry: !pinCalibrationChanged);
    }

    /// <summary>
    /// Logs one localized hint per component whose pin calibration changed since the
    /// design was saved and re-routes the affected — now path-less — connections.
    /// </summary>
    private void ReportPinCalibrationMigrations()
    {
        if (_pinCalibrationMigratedComponents.Count == 0)
            return;

        foreach (var name in _pinCalibrationMigratedComponents.OrderBy(n => n, StringComparer.Ordinal))
        {
            _errorConsole?.LogWarning(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Services.Localization.LocalizationService.Instance.Translate("Load.PinCalibrationChanged"),
                name));
        }
        _pinCalibrationMigratedComponents.Clear();
        _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>
    /// Restores per-connection routing style, width/radius and freeze state from saved data.
    /// <paramref name="keepFrozenGeometry"/> is false when the cached geometry was
    /// discarded by the pin-calibration migration — the freeze flag and the per-bend
    /// overrides describe exactly that stale geometry and must not survive it.
    /// </summary>
    private static void RestoreRoutingSettings(
        WaveguideConnection connection, ConnectionData connData, bool keepFrozenGeometry = true)
    {
        // Legacy styles removed from WaveguideType: "Euler" was drawn as the same generous
        // arc as Bend (migrate to Bend); "Straight" (and any other unknown name) falls back
        // to Auto by simply not parsing — the connection keeps its default Type.
        if (connData.RoutingStyle == "Euler")
            connection.Type = WaveguideType.Bend;
        else if (connData.RoutingStyle != null &&
            Enum.TryParse<WaveguideType>(connData.RoutingStyle, ignoreCase: false, out var style) &&
            Enum.IsDefined(style))
            connection.Type = style;
        if (connData.WidthMicrometers.HasValue)
            connection.WidthMicrometers = connData.WidthMicrometers.Value;
        if (connData.BendRadiusMicrometers.HasValue)
            connection.BendRadiusMicrometers = connData.BendRadiusMicrometers.Value;
        if (keepFrozenGeometry && connData.IsRouteFrozen == true)
            connection.IsRouteFrozen = true;
        if (keepFrozenGeometry && connData.BendRadiusOverrides != null)
        {
            foreach (var (bendIndex, radius) in connData.BendRadiusOverrides)
                connection.BendRadiusOverrides[bendIndex] = radius;
        }
        if (keepFrozenGeometry && connData.StraightShiftOffsets != null)
        {
            foreach (var (straightIndex, offset) in connData.StraightShiftOffsets)
                connection.StraightShiftOffsets[straightIndex] = offset;
        }
    }

    /// <summary>
    /// When the design contains gdsfactory-native components (a Nazca script can't express
    /// them), asks the user whether to export to Nazca anyway (omitting them) or switch to the
    /// gdsfactory export. Returns true to proceed with the Nazca export, false to cancel.
    /// A pure Nazca design (or a headless run with no message box) proceeds without prompting.
    /// </summary>
    private async Task<bool> ConfirmNazcaExportDropsGdsFactoryComponentsAsync()
    {
        var gdsFactory = CAP.Avalonia.Services.GdsFactoryExport.NazcaExportGuard
            .CollectGdsFactoryNativeComponents(_canvas);
        if (gdsFactory.Count == 0 || MessageBoxService == null)
            return true;

        var message =
            $"{gdsFactory.Count} component(s) in this design are gdsfactory-native (e.g. CornerStone "
            + "SiN) and cannot be written to a Nazca script — they would be omitted from the export. "
            + "Use the gdsfactory export to include them.\n\nExport to Nazca anyway?";
        const int switchToGdsFactoryIndex = 0;
        const int exportAnywayIndex = 1;
        var choice = await MessageBoxService.ShowChoicePromptAsync(
            message, "gdsfactory components will be omitted",
            new[] { "Use gdsfactory export instead", "Export to Nazca anyway" });

        if (choice == exportAnywayIndex)
            return true;

        // "Use gdsfactory export instead" — cancel this Nazca export and open the gdsfactory
        // export flow so the user isn't left with nothing happening. A dismissed dialog just cancels.
        if (choice == switchToGdsFactoryIndex && RequestGdsFactoryExport != null)
            await RequestGdsFactoryExport();
        else
            UpdateStatus?.Invoke("Nazca export cancelled — use the gdsfactory export for this design.");
        return false;
    }

    [RelayCommand]
    private async Task ExportNazca()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Export not available");
            return;
        }

        if (_canvas.Components.Count == 0)
        {
            UpdateStatus?.Invoke("Nothing to export - add some components first");
            return;
        }

        // A gdsfactory-native design (e.g. CornerStone SiN) cannot be expressed in a Nazca
        // script — those components would be silently omitted. Make the user choose consciously:
        // continue anyway, or switch to the gdsfactory export that can include them (#570).
        if (!await ConfirmNazcaExportDropsGdsFactoryComponentsAsync())
            return;

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to Nazca Python",
            "py",
            "Python Files|*.py|All Files|*.*");

        if (filePath != null)
        {
            // A script named like a Python module it imports (e.g. re.py, numpy.py)
            // shadows that module and fails with a cryptic circular-import error —
            // refuse the name up front instead of letting the Nazca run explode.
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (PythonModuleShadowing.ShadowsPythonModule(stem))
            {
                var warning = $"'{Path.GetFileName(filePath)}' shadows the Python module '{stem.ToLowerInvariant()}' "
                    + "— the exported script could not import Nazca. Please choose a different file name (e.g. chip1.py).";
                if (MessageBoxService != null)
                    await MessageBoxService.ShowChoicePromptAsync(warning, "Invalid script name", new[] { "OK" });
                UpdateStatus?.Invoke(warning);
                return;
            }

            try
            {
                // Collected by the exporter as a side effect of writing the script below —
                // connections/frozen paths whose route is a placeholder or invalid never render
                // as GDS geometry (a self-crossing fallback has no optical model; invalid
                // geometry violates the bend radius); connections whose sibling-crossing flag no
                // bridge marker resolves still render but deserve a second look. Reading both
                // AFTER the write (rather than recomputing from a live canvas snapshot
                // beforehand) guarantees the report matches exactly what landed in the script,
                // even while background routing is still in flight.
                var skippedConnectionsList = new List<string>();
                var unresolvedCrossingsList = new List<string>();
                var exportWarningsList = new List<string>();
                var nazcaCode = _nazcaExporter.Export(
                    _canvas, metalSpec: MetalRoutingSpecProvider?.Invoke(),
                    skippedConnections: skippedConnectionsList, unresolvedCrossings: unresolvedCrossingsList,
                    library: _componentLibrary, exportWarnings: exportWarningsList);
                await File.WriteAllTextAsync(filePath, nazcaCode);

                // Raw-code components whose geometry source vanished (a deleted .gds) exported
                // as placeholder boxes — say so plainly instead of silently shipping the stub:
                // each description in full to the (copyable) Error Console, the aggregated
                // count additionally prefixed onto the final status via WithWarnings below,
                // so it is visible without watching the console.
                foreach (var exportWarning in exportWarningsList)
                    _errorConsole?.LogWarning(exportWarning);

                var skippedConnectionsWarning = ExportWarningMessages.BuildSkipped(skippedConnectionsList);
                var unresolvedCrossingsWarning = ExportWarningMessages.BuildUnresolvedCrossings(unresolvedCrossingsList);
                var missingSourcesWarning = ExportWarningMessages.BuildMissingGdsSources(exportWarningsList);
                if (skippedConnectionsWarning != null)
                    _errorConsole?.LogWarning(skippedConnectionsWarning);
                if (unresolvedCrossingsWarning != null)
                    _errorConsole?.LogWarning(unresolvedCrossingsWarning);

                // GDS pre-flight: refresh a stale "not ready" verdict once, then ask the
                // user how to proceed when Nazca is genuinely unavailable.
                if (GdsExport.GenerateGdsEnabled && !GdsExport.IsEnvironmentReady)
                    await GdsExport.CheckEnvironmentAsync();

                var decision = await GdsExport.PreflightGdsAsync(MessageBoxService);
                if (decision != Export.GdsPreflightDecision.Proceed)
                {
                    await HandleSkippedGdsAsync(
                        decision, filePath, skippedConnectionsWarning, unresolvedCrossingsWarning,
                        missingSourcesWarning);
                    return;
                }

                // Attempt GDS generation if enabled
                var result = await GdsExport.ExportScriptToGdsAsync(filePath);

                if (result.Success && result.GdsPath != null)
                {
                    UpdateStatus?.Invoke(WithWarnings(
                        $"Exported {Path.GetFileName(filePath)} and {Path.GetFileName(result.GdsPath)}",
                        skippedConnectionsWarning, unresolvedCrossingsWarning, missingSourcesWarning));

                    // Try to open the generated GDS file in the default viewer (KLayout etc.) —
                    // this is a content launch, not a file-manager open, so it stays useful even
                    // when the user runs many exports back-to-back.
                    TryOpenFileWithDefaultApp(result.GdsPath);
                }
                else if (result.Success)
                {
                    UpdateStatus?.Invoke(WithWarnings(
                        $"Exported to {Path.GetFileName(filePath)}",
                        skippedConnectionsWarning, unresolvedCrossingsWarning, missingSourcesWarning));
                }
                else
                {
                    // Log full Python error to Error Console for visibility
                    _errorConsole?.LogError($"GDS generation failed: {result.ErrorMessage}");
                    UpdateStatus?.Invoke(WithWarnings(
                        $"Exported {Path.GetFileName(filePath)} (GDS generation failed: {result.ErrorMessage})",
                        skippedConnectionsWarning, unresolvedCrossingsWarning, missingSourcesWarning));
                }
            }
            catch (Exception ex)
            {
                _errorConsole?.LogError($"Failed to export Nazca design: {ex.Message}", ex);
                UpdateStatus?.Invoke($"Export failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Handles the non-Proceed pre-flight outcomes: the Nazca script is already on disk,
    /// only the GDS step is skipped. Install/settings choices open the Settings window on
    /// the Python-Environments page (the install progress is visible there).
    /// </summary>
    private async Task HandleSkippedGdsAsync(
        Export.GdsPreflightDecision decision, string scriptPath,
        string? skippedConnectionsWarning = null, string? unresolvedCrossingsWarning = null,
        string? missingSourcesWarning = null)
    {
        if (decision == Export.GdsPreflightDecision.InstallRequested)
        {
            GdsExport.InstallNazcaCommand.Execute(null);
            if (ShowSettingsWindow != null)
                await ShowSettingsWindow(typeof(Settings.PythonEnvironmentsSettingsPage));
            UpdateStatus?.Invoke(WithWarnings(
                $"Exported {Path.GetFileName(scriptPath)} — GDS skipped (installing Nazca)",
                skippedConnectionsWarning, unresolvedCrossingsWarning, missingSourcesWarning));
            return;
        }

        if (decision == Export.GdsPreflightDecision.OpenSettingsRequested
            && ShowSettingsWindow != null)
            await ShowSettingsWindow(typeof(Settings.PythonEnvironmentsSettingsPage));

        UpdateStatus?.Invoke(WithWarnings(
            $"Exported {Path.GetFileName(scriptPath)} — GDS skipped (Nazca not available)",
            skippedConnectionsWarning, unresolvedCrossingsWarning, missingSourcesWarning));
    }

    /// <summary>Prefixes a status line with any non-null warnings, in order, so they survive
    /// next to the final result instead of being scrolled away.</summary>
    private static string WithWarnings(string status, params string?[] warnings)
    {
        var lines = warnings.Where(w => w != null).Append(status);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Exports the current design to a SAX/Simphony-compatible Python
    /// simulation script. Historically labelled "PICWave" because issue #474
    /// requested that target — the implementation always emitted sax-based
    /// Python (see <c>SaxScriptWriter</c>). Renamed so the UI label, file
    /// header and status messages all describe the actual output.
    /// </summary>
    [RelayCommand]
    private async Task ExportSax()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Export not available");
            return;
        }

        if (_canvas.Components.Count == 0)
        {
            UpdateStatus?.Invoke("Nothing to export - add some components first");
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to SAX (Simphony) Python",
            "py",
            "Python Files|*.py|All Files|*.*");

        if (filePath == null)
            return;

        try
        {
            var components = _canvas.Components.Select(vm => vm.Component);
            var connections = _canvas.Connections.Select(vm => vm.Connection);
            var script = _saxExporter.Export(components, connections);
            await File.WriteAllTextAsync(filePath, script);
            UpdateStatus?.Invoke($"Exported SAX script: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to export SAX script: {ex.Message}", ex);
            UpdateStatus?.Invoke($"SAX export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a 90° counter-clockwise rotation to a component.
    /// </summary>
    private static void ApplyRotationToComponent(Component comp)
    {
        var width = comp.WidthMicrometers;
        var height = comp.HeightMicrometers;

        foreach (var pin in comp.PhysicalPins)
        {
            var cx = width / 2;
            var cy = height / 2;
            var x = pin.OffsetXMicrometers - cx;
            var y = pin.OffsetYMicrometers - cy;
            var newX = -y;
            var newY = x;
            pin.OffsetXMicrometers = newX + cy;
            pin.OffsetYMicrometers = newY + cx;
        }

        comp.WidthMicrometers = height;
        comp.HeightMicrometers = width;
        comp.RotateBy90CounterClockwise();
    }

    /// <summary>
    /// Attempts to open a file with the system's default application.
    /// If no default app exists, opens the file explorer and selects the file.
    /// </summary>
    /// <param name="filePath">Path to the file to open.</param>
    private void TryOpenFileWithDefaultApp(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            // Open with the default application via the platform launcher.
            // Falls back to revealing in the file manager if no handler is registered.
            try
            {
                _urlLauncher.OpenFileOrDirectory(filePath);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _errorConsole?.LogWarning($"No default app for {Path.GetFileName(filePath)} ({ex.Message}). Falling back to file explorer.");
                OpenFileExplorer(filePath);
            }
        }
        catch (Exception ex)
        {
            _errorConsole?.LogWarning($"Could not open GDS file: {ex.Message}");
        }
    }

    /// <summary>
    /// Reveals the specified file in the system file manager.
    /// Works cross-platform via <see cref="IUrlLauncher.RevealInFileManager"/>.
    /// </summary>
    /// <param name="filePath">Path to the file to reveal.</param>
    private void OpenFileExplorer(string filePath)
    {
        try
        {
            var absolutePath = Path.GetFullPath(filePath);
            _urlLauncher.RevealInFileManager(absolutePath);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogWarning($"Could not open file explorer: {ex.Message}");
        }
    }

}

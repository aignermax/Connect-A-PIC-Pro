using System.Collections.ObjectModel;
using System.Text.Json;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components;
using CAP_Core.Components.Core;
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

    /// <summary>
    /// Current .lun format version this build reads and writes. Files with any other value are rejected at load time.
    /// </summary>
    private const string CurrentFormatVersion = "2.0";

    private string? _currentFilePath;

    /// <summary>
    /// Persists metadata loaded from the last opened file so that Created date
    /// and other user-set fields survive a save-over-reload cycle.
    /// </summary>
    private DesignMetadata? _loadedMetadata;

    /// <summary>
    /// Per-component S-matrix overrides loaded from the PIR section of the .lun file,
    /// or added via the S-parameter import feature. Survives save-over-reload cycles.
    /// Keyed by component identifier string; values are the stored S-matrices.
    /// </summary>
    public Dictionary<string, ComponentSMatrixData> StoredSMatrices { get; } = new();

    /// <summary>
    /// Per-instance Nazca function parameter overrides. Keyed by component Identifier;
    /// values hold the override and the original template values for reset.
    /// Serialised to/from the .lun file's <c>NazcaOverrides</c> section.
    /// </summary>
    public Dictionary<string, CAP_DataAccess.Persistence.PIR.NazcaCodeOverride> StoredNazcaOverrides { get; } = new();

    [ObservableProperty]
    private bool _hasUnsavedChanges;

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
        IUrlLauncher? urlLauncher = null)
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

        // Track changes to mark project as unsaved
        _canvas.Components.CollectionChanged += (s, e) => HasUnsavedChanges = true;
        _canvas.Connections.CollectionChanged += (s, e) => HasUnsavedChanges = true;

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

        // Reuse ApplyAll with a single-component view so the identifier /
        // template-key lookup logic stays in one place. Re-applying the
        // same matrix to an already-up-to-date component is a no-op.
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

        ApplyUserGlobalOverrides(addedComponents);
        ApplyAllNazcaOverrides(addedComponents);
    }

    /// <summary>
    /// Applies the user-global PDK template S-matrix overrides to a set of
    /// components. Project-local instance overrides (in <see cref="StoredSMatrices"/>)
    /// take precedence and are intentionally applied first; this fills the
    /// gap for components that don't have a project-local override.
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

    [RelayCommand]
    private async Task SaveDesign()
    {
        if (FileDialogService == null)
        {
            UpdateStatus?.Invoke("Save not available");
            return;
        }

        var filePath = _currentFilePath ?? await FileDialogService.ShowSaveFileDialogAsync(
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
                        IsLocked = c.Connection.IsLocked ? true : null,
                        TargetLengthMicrometers = c.Connection.TargetLengthMicrometers,
                        IsTargetLengthEnabled = c.Connection.IsTargetLengthEnabled ? true : null,
                        LengthToleranceMicrometers = c.Connection.IsTargetLengthEnabled ? c.Connection.LengthToleranceMicrometers : null
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
            if (StoredNazcaOverrides.Count > 0)
                designData.NazcaOverrides = new Dictionary<string, CAP_DataAccess.Persistence.PIR.NazcaCodeOverride>(StoredNazcaOverrides);
            designData.ChipWidthMicrometers  = _canvas.ChipMaxX;
            designData.ChipHeightMicrometers = _canvas.ChipMaxY;

            var json = JsonSerializer.Serialize(designData, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            await File.WriteAllTextAsync(filePath, json);
            _currentFilePath = filePath;
            HasUnsavedChanges = false;
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
            SliderValue = c.HasSliders ? c.SliderValue : null,
            LaserWavelengthNm = c.LaserConfig?.WavelengthNm,
            LaserPower = c.LaserConfig?.InputPower,
            IsLocked = c.Component.IsLocked ? true : null,
            HumanReadableName = c.Component.HumanReadableName
        };
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
    private string? ResolveTemplateKey(Component component)
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
        CAP.Avalonia.Services.ComponentGeometryKey.For(
            component,
            c => StoredNazcaOverrides.TryGetValue(c.Identifier, out var o) ? o.RawCode : null);

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
    private string? FindTemplatePdkSource(Component component)
    {
        var nazcaFunc = component.NazcaFunctionName;
        if (string.IsNullOrEmpty(nazcaFunc))
            return null;

        var match = _componentLibrary.FirstOrDefault(t =>
        {
            var templateFunc = t.NazcaFunctionName
                ?? $"nazca_{t.Name.ToLower().Replace(" ", "_")}";
            return templateFunc == nazcaFunc;
        });
        return match?.PdkSource;
    }

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
                SliderValue = child.GetAllSliders().Count > 0
                    ? child.GetSlider(0)?.Value : null,
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

        var filePath = await FileDialogService.ShowOpenFileDialogAsync(
            "Load Design",
            "Lunima Files|*.lun|All Files|*.*");

        if (filePath != null)
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var designData = JsonSerializer.Deserialize<DesignFileData>(json);

                if (designData == null)
                {
                    UpdateStatus?.Invoke("Invalid design file");
                    return;
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

                // Preserve PIR metadata so Created date survives subsequent saves
                _loadedMetadata = designData.Metadata;

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

                    // Apply per-instance overrides to live components so the
                    // next simulation run picks up the stored S-matrices.
                    // Falls back to "{pdkSource}::{templateName}" so PDK-template-scoped
                    // overrides reach every instance of the template, not just renamed instances.
                    var allComponents = _canvas.Components.Select(vm => vm.Component).ToList();
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
                    ApplyUserGlobalOverrides(allComponents);
                }
                else
                {
                    // No project overrides — still apply user-global ones so the
                    // user's PDK template edits show up in projects that never
                    // had any project-scoped overrides of their own.
                    ApplyUserGlobalOverrides(_canvas.Components.Select(vm => vm.Component));
                }

                // Restore per-instance Nazca overrides and apply them to live components
                StoredNazcaOverrides.Clear();
                if (designData.NazcaOverrides != null)
                {
                    foreach (var kv in designData.NazcaOverrides)
                        StoredNazcaOverrides[kv.Key] = kv.Value;

                    ApplyAllNazcaOverrides(_canvas.Components.Select(vm => vm.Component));
                }

                _currentFilePath = filePath;
                HasUnsavedChanges = false;
                UpdateStatus?.Invoke($"Loaded {Path.GetFileName(filePath)} ({_canvas.Components.Count} components, {_canvas.Connections.Count} connections, {groupCount} groups)");
                _commandManager.NotifyStateChanged();

                // Rebuild hierarchy tree after loading
                RebuildHierarchy?.Invoke();

                // Auto zoom-to-fit after loading
                ZoomToFitAfterLoad?.Invoke(900, 800);
            }
            catch (Exception ex)
            {
                _errorConsole?.LogError($"Failed to load design: {ex.Message}", ex);
                UpdateStatus?.Invoke($"Load failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Creates a new empty project, prompting to save if there are unsaved changes.
    /// Exits group edit mode if active before clearing the canvas.
    /// </summary>
    [RelayCommand]
    private async Task NewProject()
    {
        // Check if there are unsaved changes
        if (HasUnsavedChanges && MessageBoxService != null)
        {
            var result = await MessageBoxService.ShowSavePromptAsync(
                "Do you want to save your changes before creating a new project?",
                "Save Changes?");

            if (result == SavePromptResult.Save)
            {
                await SaveDesign();

                // Check if save was actually performed (user might have cancelled)
                if (HasUnsavedChanges)
                {
                    // User cancelled the save dialog, so cancel new project
                    return;
                }
            }
            else if (result == SavePromptResult.Cancel)
            {
                // User cancelled, do nothing
                return;
            }
            // DontSave: continue to clear
        }

        // Exit group edit mode if active
        if (_canvas.IsInGroupEditMode)
        {
            _canvas.ExitToRoot();
        }

        // Clear the canvas
        ClearCanvas();

        _currentFilePath = null;
        _loadedMetadata = null;
        HasUnsavedChanges = false;
        UpdateStatus?.Invoke("New project created");

        // Rebuild hierarchy
        RebuildHierarchy?.Invoke();
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
        _commandManager.ClearHistory();
        StoredSMatrices.Clear();
        StoredNazcaOverrides.Clear();
    }

    /// <summary>
    /// Applies any stored per-instance Nazca overrides to the given components.
    /// Called on project load and when a new component is added to the canvas.
    /// </summary>
    private void ApplyAllNazcaOverrides(IEnumerable<Component> components)
    {
        if (StoredNazcaOverrides.Count == 0)
            return;

        var pinChangedComponents = new List<Component>();
        foreach (var component in components)
        {
            if (StoredNazcaOverrides.TryGetValue(component.Identifier, out var nazcaOverride))
            {
                component.NazcaFunctionName = nazcaOverride.FunctionName ?? component.NazcaFunctionName;
                component.NazcaFunctionParameters = nazcaOverride.FunctionParameters ?? component.NazcaFunctionParameters;
                if (nazcaOverride.ModuleName != null)
                    component.NazcaModuleName = nazcaOverride.ModuleName;

                // Issue #556: a raw-code override recomputes the component's size.
                // Restore the persisted bbox-derived dimensions so the canvas thumbnail
                // and layout reflect the edited geometry on load.
                if (nazcaOverride.OverrideWidthMicrometers is { } w)
                    component.WidthMicrometers = w;
                if (nazcaOverride.OverrideHeightMicrometers is { } h)
                    component.HeightMicrometers = h;

                // Issue #561: a raw-code override may also redefine the component's ports.
                // Restore the persisted override pins so in-app connections and export use
                // the correct port layout after project load.
                if (nazcaOverride.OverridePins?.Count > 0)
                {
                    OverridePinMapper.ApplyPinsToComponent(component, nazcaOverride.OverridePins);
                    pinChangedComponents.Add(component);
                }
            }
        }

        // Connections (and the canvas pin view-models) were created against the
        // template pins BEFORE the override replaced them, so they hold stale pin
        // objects — the GDS export would then reference pins the override cell does
        // not define. Re-anchor them onto the same-named new pins, drop the rest.
        foreach (var component in pinChangedComponents)
        {
            var warnings = _canvas.OnComponentPinsChanged(component);
            foreach (var warning in warnings)
                _errorConsole?.LogWarning(warning);
        }
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

        // Apply rotation
        for (int i = 0; i < compData.Rotation; i++)
        {
            ApplyRotationToComponent(component);
        }

        var vm = _canvas.AddComponent(component, template.Name, template.PdkSource);

        // Restore slider value
        if (compData.SliderValue.HasValue && vm.HasSliders)
            vm.SliderValue = compData.SliderValue.Value;

        // Restore laser configuration
        if (vm.LaserConfig != null)
        {
            if (compData.LaserWavelengthNm.HasValue)
                vm.LaserConfig.WavelengthNm = compData.LaserWavelengthNm.Value;
            if (compData.LaserPower.HasValue)
                vm.LaserConfig.InputPower = compData.LaserPower.Value;
        }

        // Restore lock state
        if (compData.IsLocked == true)
            component.IsLocked = true;

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

                // Apply rotation
                for (int i = 0; i < childData.Rotation; i++)
                {
                    ApplyRotationToComponent(child);
                }

                // Restore slider
                if (childData.SliderValue.HasValue && child.GetAllSliders().Count > 0)
                {
                    var slider = child.GetSlider(0);
                    if (slider != null) slider.Value = childData.SliderValue.Value;
                }

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
            connData.CachedSegments, connData.IsBlockedFallback ?? false);

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

        // Restore target length configuration
        if (connVm != null)
        {
            if (connData.TargetLengthMicrometers.HasValue)
                connVm.Connection.TargetLengthMicrometers = connData.TargetLengthMicrometers.Value;
            if (connData.IsTargetLengthEnabled == true)
                connVm.Connection.IsTargetLengthEnabled = true;
            if (connData.LengthToleranceMicrometers.HasValue)
                connVm.Connection.LengthToleranceMicrometers = connData.LengthToleranceMicrometers.Value;
        }
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

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to Nazca Python",
            "py",
            "Python Files|*.py|All Files|*.*");

        if (filePath != null)
        {
            try
            {
                // Export Python script
                var nazcaCode = _nazcaExporter.Export(_canvas, overrides: StoredNazcaOverrides);
                await File.WriteAllTextAsync(filePath, nazcaCode);

                // Attempt GDS generation if enabled
                var result = await GdsExport.ExportScriptToGdsAsync(filePath);

                if (result.Success && result.GdsPath != null)
                {
                    UpdateStatus?.Invoke($"Exported {Path.GetFileName(filePath)} and {Path.GetFileName(result.GdsPath)}");

                    // Try to open the generated GDS file in the default viewer (KLayout etc.) —
                    // this is a content launch, not a file-manager open, so it stays useful even
                    // when the user runs many exports back-to-back.
                    TryOpenFileWithDefaultApp(result.GdsPath);
                }
                else if (result.Success)
                {
                    UpdateStatus?.Invoke($"Exported to {Path.GetFileName(filePath)}");
                }
                else
                {
                    // Log full Python error to Error Console for visibility
                    _errorConsole?.LogError($"GDS generation failed: {result.ErrorMessage}");
                    UpdateStatus?.Invoke($"Exported {Path.GetFileName(filePath)} (GDS generation failed: {result.ErrorMessage})");
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

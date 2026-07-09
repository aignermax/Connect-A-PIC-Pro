using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Lets the user view and adjust the fabrication process behind a PDK: its layer
/// stack, waveguide/metal cross-sections (widths + bend radii) and materials.
/// A process can be imported from any supported foundry format (openEPDA uPDK YAML,
/// Nazca CSV tables) or built by hand. First slice of issue #570.
/// </summary>
public partial class ProcessManagementViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialog;
    private readonly IReadOnlyList<IProcessImporter> _importers;
    private readonly PdkJsonSaver _pdkSaver;

    /// <summary>Member PDK drafts of the active process, captured on open, used to persist edits.</summary>
    private IReadOnlyList<PdkDraft> _memberDrafts = new List<PdkDraft>();

    /// <summary>
    /// Names of the layer/cross-section/material rows that belong to the currently loaded member
    /// PDK(s), as opposed to rows pulled in later by <see cref="ImportFromPdk"/>'s <see cref="Merge"/>
    /// from an unrelated reference PDK. <see cref="SaveProcess"/> only ever writes rows in these
    /// sets, so an ad-hoc reference import can never corrupt the member PDK's own layer stack
    /// (issue #686 review, Finding 2). Populated by <see cref="Load"/> / <see cref="ShowLockedProcess"/>
    /// and by the manual Add* commands; a later <see cref="Merge"/> call (the import path) does
    /// NOT add to these sets.
    /// </summary>
    private readonly HashSet<string> _ownLayerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cross-section names owned by the loaded member PDK(s); see <see cref="_ownLayerNames"/>.</summary>
    private readonly HashSet<string> _ownXsectionNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Material names owned by the loaded member PDK(s); see <see cref="_ownLayerNames"/>.</summary>
    private readonly HashSet<string> _ownMaterialNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cross-section kinds offered in the editor (Optical / Metal).</summary>
    public static IReadOnlyList<XsectionKind> XsectionKinds { get; } =
        new[] { XsectionKind.Optical, XsectionKind.Metal };

    /// <summary>
    /// Bundled/loaded PDKs whose fabrication process can be loaded into the editor as a preset
    /// (issue #570 follow-up): SiEPIC EBeam, CornerStone SiN, Demo, and any other loaded PDK that
    /// declares a <see cref="ProcessDefinition"/>. Populated by <see cref="SetAvailablePresets"/>,
    /// which <see cref="ShowActiveProcess"/> calls with every loaded PDK each time the dialog opens.
    /// </summary>
    public ObservableCollection<PdkDraft> AvailablePresets { get; } = new();

    /// <summary>
    /// The preset picked in the "Use preset" dropdown. Selecting one USES that PDK's process
    /// as the design's active fabrication process via <see cref="OnSelectedPresetChanged"/>
    /// (implemented in <c>ProcessManagementViewModel.PresetUse.cs</c>, issue #696).
    /// </summary>
    [ObservableProperty]
    private PdkDraft? _selectedPreset;

    /// <summary>Resolves a PDK name to its source JSON path so edits can be persisted; wired by the
    /// UI layer to the loaded-PDK registry. Null (e.g. in tests/headless) disables saving.</summary>
    public Func<string, string?>? PdkFilePathResolver { get; set; }

    /// <summary>Name of the loaded process.</summary>
    [ObservableProperty]
    private string _processName = string.Empty;

    /// <summary>Status / result message.</summary>
    [ObservableProperty]
    private string _statusText = "No process loaded. Import a PDK (uPDK YAML or Nazca CSV) or start a new one.";

    /// <summary>True once a process is loaded (drives the grids' visibility).</summary>
    [ObservableProperty]
    private bool _hasProcess;

    /// <summary>Editable layer stack.</summary>
    public ObservableCollection<ProcessLayer> Layers { get; } = new();

    /// <summary>Editable cross-sections (waveguide + metal).</summary>
    public ObservableCollection<ProcessXsection> Xsections { get; } = new();

    /// <summary>Editable materials.</summary>
    public ObservableCollection<ProcessMaterial> Materials { get; } = new();

    /// <summary>Initialises the ViewModel with the default importer set.</summary>
    public ProcessManagementViewModel(IFileDialogService fileDialog)
        : this(fileDialog, new IProcessImporter[]
        {
            new UpdkYamlProcessImporter(),
            new NazcaCsvProcessImporter(),
        })
    {
    }

    /// <summary>Initialises the ViewModel with a specific importer set (tests).</summary>
    public ProcessManagementViewModel(
        IFileDialogService fileDialog, IReadOnlyList<IProcessImporter> importers, PdkJsonSaver? pdkSaver = null)
    {
        _fileDialog = fileDialog;
        _importers = importers;
        _pdkSaver = pdkSaver ?? new PdkJsonSaver();
    }

    /// <summary>Populates the editable collections from a process definition.</summary>
    public void Load(ProcessDefinition process)
    {
        ProcessName = process.Name;
        Replace(Layers, process.Layers);
        Replace(Xsections, process.Xsections);
        Replace(Materials, process.Materials);
        MarkAllRowsOwned();
        HasProcess = true;
    }

    /// <summary>
    /// Refreshes <see cref="AvailablePresets"/> from the currently loaded PDKs: any PDK that
    /// declares a <see cref="ProcessDefinition"/> (bundled or user-loaded) can seed the editor.
    /// Called by <see cref="ShowActiveProcess"/> every time the dialog opens, so the picker
    /// always reflects the live PDK set instead of a stale snapshot.
    /// </summary>
    public void SetAvailablePresets(IReadOnlyList<PdkDraft> loadedPdks)
    {
        AvailablePresets.Clear();
        foreach (var pdk in loadedPdks.Where(p => p.Process != null))
            AvailablePresets.Add(pdk);
    }

    /// <summary>
    /// Snapshots the names currently in <see cref="Layers"/>/<see cref="Xsections"/>/<see cref="Materials"/>
    /// as belonging to the process being edited (issue #686 review, Finding 2) — called right after
    /// the collections are populated from the process's OWN definition(s), before any later
    /// <see cref="ImportFromPdk"/> reference import can add unrelated rows.
    /// </summary>
    private void MarkAllRowsOwned()
    {
        _ownLayerNames.Clear();
        _ownXsectionNames.Clear();
        _ownMaterialNames.Clear();
        foreach (var layer in Layers)
            if (layer.Name != null) _ownLayerNames.Add(layer.Name);
        foreach (var xs in Xsections)
            if (xs.Name != null) _ownXsectionNames.Add(xs.Name);
        foreach (var mat in Materials)
            if (mat.Name != null) _ownMaterialNames.Add(mat.Name);
    }

    /// <summary>Builds a process definition from the current editable state.</summary>
    public ProcessDefinition ToProcess() => new()
    {
        Name = ProcessName,
        Layers = Layers.ToList(),
        Xsections = Xsections.ToList(),
        Materials = Materials.ToList(),
    };

    /// <summary>
    /// Imports a process from a PDK file. The format is auto-detected: an openEPDA
    /// uPDK YAML blueprint or a Nazca CSV table (the user picks any CSV in the folder).
    /// </summary>
    [RelayCommand]
    private async Task ImportFromPdk()
    {
        var path = await _fileDialog.ShowOpenFileDialogAsync(
            "Select a PDK file (uPDK *.yaml, or a Nazca table_*.csv in the PDK folder)",
            "PDK Files|*.yaml;*.yml;*.csv|All Files|*.*");
        if (path == null)
            return;

        var importer = _importers.FirstOrDefault(i => i.CanImport(path));
        if (importer == null)
        {
            StatusText = $"Unsupported PDK file: {Path.GetFileName(path)}";
            return;
        }

        try
        {
            var process = importer.Import(path);
            Merge(process);
            StatusText = $"Imported '{process.Name}' via {importer.FormatName}. Now: {Layers.Count} layers, " +
                         $"{Xsections.Count} cross-sections, {Materials.Count} materials. " +
                         "Tip: uPDK has cross-sections only — import the CSV tables too for the layer stack.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed ({importer.FormatName}): {ex.Message}";
        }
    }

    /// <summary>
    /// Merges an imported process into the current one rather than replacing it,
    /// so complementary formats accumulate (uPDK supplies cross-section widths +
    /// metadata; the Nazca CSV supplies the layer stack + bend radii). Existing
    /// entries are enriched (empty fields filled) rather than duplicated.
    /// </summary>
    public void Merge(ProcessDefinition process)
    {
        if (string.IsNullOrWhiteSpace(ProcessName) || ProcessName == "New process")
            ProcessName = process.Name;

        foreach (var layer in process.Layers)
            if (Layers.All(l => !l.Name.Equals(layer.Name, StringComparison.OrdinalIgnoreCase)))
                Layers.Add(layer);

        foreach (var xs in process.Xsections)
        {
            var existing = Xsections.FirstOrDefault(x => x.Name.Equals(xs.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null) { Xsections.Add(xs); continue; }
            if (existing.WidthUm == 0) existing.WidthUm = xs.WidthUm;
            if (existing.MinRadiusUm == 0) existing.MinRadiusUm = xs.MinRadiusUm;
            if (existing.RecommendedRadiusUm == 0) existing.RecommendedRadiusUm = xs.RecommendedRadiusUm;
            if (string.IsNullOrEmpty(existing.Description)) existing.Description = xs.Description;
        }

        foreach (var mat in process.Materials)
            if (Materials.All(m => !m.Name.Equals(mat.Name, StringComparison.OrdinalIgnoreCase)))
                Materials.Add(mat);

        HasProcess = true;
    }

    /// <summary>Starts a blank process seeded with public SOI material defaults.</summary>
    [RelayCommand]
    private void NewProcess()
    {
        Load(new ProcessDefinition
        {
            Name = "New process",
            Materials = ProcessMaterialDefaults.Soi(),
        });
        StatusText = "New process started with public SOI material defaults. Add layers and cross-sections.";
    }

    /// <summary>Adds an empty layer row for manual entry.</summary>
    [RelayCommand]
    private void AddLayer()
    {
        var layer = new ProcessLayer { Name = "NEW_LAYER" };
        Layers.Add(layer);
        _ownLayerNames.Add(layer.Name);
    }

    /// <summary>Adds an empty cross-section row for manual entry.</summary>
    [RelayCommand]
    private void AddXsection()
    {
        var xsection = new ProcessXsection { Name = "new_xs" };
        Xsections.Add(xsection);
        _ownXsectionNames.Add(xsection.Name);
    }

    /// <summary>
    /// Adds a metal cross-section preset for electrical routing (issue #682) plus a matching
    /// METAL layer if the stack has none, so a user can define electrical routing in one click.
    /// </summary>
    [RelayCommand]
    private void AddMetalXsection()
    {
        // A legacy/imported row can have a null Name despite the DTO's non-nullable declaration
        // (e.g. deserialized from JSON that omitted "name") — guard like the resolver does
        // (issue #686 review, Finding 3) so this command doesn't throw an NRE.
        if (Layers.All(l => l.Name == null || !l.Name.Contains("METAL", StringComparison.OrdinalIgnoreCase)))
        {
            var metalLayer = new ProcessLayer
            {
                Name = "METAL-1", Layer = MetalTraceStyle.DefaultGdsLayer, Datatype = 0,
                Description = "Electrical routing metal",
            };
            Layers.Add(metalLayer);
            _ownLayerNames.Add(metalLayer.Name);
        }

        var metalXsection = new ProcessXsection
        {
            Name = "metal",
            Kind = XsectionKind.Metal,
            WidthUm = MetalTraceStyle.DefaultWidthUm,
            Layers = { "METAL-1" },
            Description = "Electrical routing trace",
        };
        Xsections.Add(metalXsection);
        _ownXsectionNames.Add(metalXsection.Name);
        HasProcess = true;
        StatusText = "Added a metal cross-section for electrical routing. Set its width/layer, then Save.";
    }

    /// <summary>
    /// Persists the edited process back to its PDK JSON so the export picks up the metal
    /// cross-section (issue #682). Only unambiguous single-member processes are written; the
    /// PDK's fingerprint fields (thickness, materials, angles) are preserved — only the layer
    /// stack, cross-sections and materials are updated, and only the rows that belong to this
    /// member PDK (<see cref="_ownLayerNames"/> etc.) — an unrelated PDK pulled in via
    /// <see cref="ImportFromPdk"/> for reference must never be written into this PDK's file
    /// (issue #686 review, Finding 2).
    /// </summary>
    /// <summary>
    /// Optional confirmation gate before writing the process back to a PDK file on disk. The UI
    /// wires this to a yes/no prompt naming the target file, so a user cannot overwrite a PDK's
    /// JSON by accident. Receives the file path; returns true to proceed. Null (tests/headless)
    /// proceeds without prompting.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmSaveToPdk { get; set; }

    [RelayCommand]
    private async Task SaveProcess()
    {
        if (_memberDrafts.Count == 0)
        {
            StatusText = "Nothing to save — this process has no editable member PDK (Playground or import-only).";
            return;
        }
        if (_memberDrafts.Count > 1)
        {
            StatusText = "This process merges several PDKs; pick which one owns the edit by saving to that PDK "
                       + "directly (multi-PDK target selection is not implemented yet).";
            return;
        }

        var draft = _memberDrafts[0];
        var path = PdkFilePathResolver?.Invoke(draft.Name);
        if (string.IsNullOrEmpty(path))
        {
            StatusText = $"Could not locate the PDK file for '{draft.Name}' — save unavailable.";
            return;
        }

        // Writing edits back to a PDK's JSON on disk is a deliberate act — confirm first so a
        // real PDK cannot be overwritten by accident (user field feedback). Only this process's
        // own rows are written regardless; the prompt makes the file target explicit.
        if (ConfirmSaveToPdk != null && !await ConfirmSaveToPdk(path))
        {
            StatusText = "Save cancelled — the PDK file was not changed.";
            return;
        }

        try
        {
            // Preserve the fingerprint-bearing fields (thickness, foundry, angles); only the
            // user-editable stack/xsections/materials change, and update the in-memory draft so
            // the next export uses the new metal cross-section without a reload.
            var process = draft.Process ?? new ProcessDefinition { Name = ProcessName };
            process.Layers = Layers.Where(l => l.Name != null && _ownLayerNames.Contains(l.Name)).ToList();
            process.Xsections = Xsections.Where(x => x.Name != null && _ownXsectionNames.Contains(x.Name)).ToList();
            process.Materials = Materials.Where(m => m.Name != null && _ownMaterialNames.Contains(m.Name)).ToList();
            draft.Process = process;

            _pdkSaver.SaveToFile(draft, path);
            StatusText = $"Saved to {Path.GetFileName(path)}. Electrical routing now uses this process's metal cross-section.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>Adds an empty material row for manual entry.</summary>
    [RelayCommand]
    private void AddMaterial()
    {
        var material = new ProcessMaterial { Name = "NewMaterial" };
        Materials.Add(material);
        _ownMaterialNames.Add(material.Name);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}

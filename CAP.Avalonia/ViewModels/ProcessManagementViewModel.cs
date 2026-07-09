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

    /// <summary>The cross-section kinds offered in the editor (Optical / Metal).</summary>
    public static IReadOnlyList<XsectionKind> XsectionKinds { get; } =
        new[] { XsectionKind.Optical, XsectionKind.Metal };

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
        HasProcess = true;
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
    private void AddLayer() => Layers.Add(new ProcessLayer { Name = "NEW_LAYER" });

    /// <summary>Adds an empty cross-section row for manual entry.</summary>
    [RelayCommand]
    private void AddXsection() => Xsections.Add(new ProcessXsection { Name = "new_xs" });

    /// <summary>
    /// Adds a metal cross-section preset for electrical routing (issue #682) plus a matching
    /// METAL layer if the stack has none, so a user can define electrical routing in one click.
    /// </summary>
    [RelayCommand]
    private void AddMetalXsection()
    {
        if (Layers.All(l => !l.Name.Contains("METAL", StringComparison.OrdinalIgnoreCase)))
            Layers.Add(new ProcessLayer { Name = "METAL-1", Layer = 11, Datatype = 0, Description = "Electrical routing metal" });

        Xsections.Add(new ProcessXsection
        {
            Name = "metal",
            Kind = XsectionKind.Metal,
            WidthUm = MetalTraceStyle.DefaultWidthUm,
            Layers = { "METAL-1" },
            Description = "Electrical routing trace",
        });
        HasProcess = true;
        StatusText = "Added a metal cross-section for electrical routing. Set its width/layer, then Save.";
    }

    /// <summary>
    /// Persists the edited process back to its PDK JSON so the export picks up the metal
    /// cross-section (issue #682). Only unambiguous single-member processes are written; the
    /// PDK's fingerprint fields (thickness, materials, angles) are preserved — only the layer
    /// stack, cross-sections and materials are updated.
    /// </summary>
    [RelayCommand]
    private void SaveProcess()
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

        try
        {
            // Preserve the fingerprint-bearing fields (thickness, foundry, angles); only the
            // user-editable stack/xsections/materials change, and update the in-memory draft so
            // the next export uses the new metal cross-section without a reload.
            var process = draft.Process ?? new ProcessDefinition { Name = ProcessName };
            process.Layers = Layers.ToList();
            process.Xsections = Xsections.ToList();
            process.Materials = Materials.ToList();
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
    private void AddMaterial() => Materials.Add(new ProcessMaterial { Name = "NewMaterial" });

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}

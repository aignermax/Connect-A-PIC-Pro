using System.Linq;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private string? _editingOriginalName;

    private string? _pendingForkSourcePath;
    private string? _pendingForkTargetPath;

    /// <summary>
    /// True while this editor session edits a bundled (read-only foundry) component whose PDK
    /// has not been forked yet: the fork is deferred to the first successful save, so closing
    /// the window without saving leaves nothing on disk.
    /// </summary>
    public bool HasPendingBundledFork => _pendingForkSourcePath is not null;

    private string? _editOriginalPdkFilePath;
    private string? _editOriginalPdkName;
    private string? _editOriginalProcessName;

    public string? MigratedFromPdkName { get; private set; }

    /// <summary>The component's original name in the PDK it was migrated out of (for library cleanup).</summary>
    public string? MigratedFromComponentName { get; private set; }

    /// <summary>The old name a same-PDK rename left behind, so the library drops the stale template.</summary>
    public string? RenamedAwayComponentName { get; private set; }

    /// <summary>
    /// With <see cref="EditingOriginalName"/> identifies the on-disk component being edited,
    /// independent of any in-progress rename — the main window keys its open-editor-window
    /// dictionary on this so a second "Edit…" click activates the existing window.
    /// </summary>
    public string? EditOriginalPdkKey => _editOriginalPdkFilePath ?? _editOriginalPdkName;

    /// <summary>The component's name at the time <see cref="LoadForEdit"/> was called.</summary>
    public string? EditingOriginalName => _editingOriginalName;

    private string? _loadedName;
    private string? _loadedCode;
    private GeometryBackend? _loadedBackend;
    private string? _loadedPdkFilePath;

    // The S-matrix stored in the PDK definition at load time. A save without a fresh compute
    // keeps it verbatim only while the geometry (code + backend) is unchanged AND the save
    // targets the SAME PDK file — an edit-save must never silently wipe real computed data,
    // but a changed geometry or a PDK migration (same process NAME is not the same process)
    // drops to black box rather than persisting stale physics.
    private PdkSMatrixDraft? _loadedSMatrixDraft;

    private bool CanKeepLoadedSMatrix =>
        IsEditMode && _loadedSMatrixDraft is not null
        && Code == _loadedCode && SelectedBackend == _loadedBackend
        && PathsEqual(SelectedCustomPdk?.FilePath, _loadedPdkFilePath);

    /// <summary>
    /// True when every pin name the loaded S-matrix references exists among the rendered pins.
    /// Synthesized edit code may render different pin names than the foundry definition —
    /// persisting the stored matrix against renamed pins would later resolve to silent
    /// zero-transmission matrices on placement.
    /// </summary>
    private bool LoadedSMatrixResolvesAgainstPins(IEnumerable<string> renderedPinNames)
    {
        if (_loadedSMatrixDraft is null)
            return false;
        var names = new HashSet<string>(renderedPinNames, StringComparer.OrdinalIgnoreCase);
        var connections = (_loadedSMatrixDraft.Connections ?? new List<SMatrixConnection>())
            .Concat(_loadedSMatrixDraft.WavelengthData?.SelectMany(e => e.Connections)
                    ?? Enumerable.Empty<SMatrixConnection>());
        return connections.All(c => names.Contains(c.FromPin) && names.Contains(c.ToPin));
    }

    /// <summary>
    /// True when the user changed any editable field since <see cref="LoadForEdit"/>. The
    /// editor dedup consults this: a stale-but-clean editor is replaced with a freshly loaded
    /// view model on a second ✏ click, while unsaved user input is never thrown away.
    /// </summary>
    public bool HasUnsavedEditChanges =>
        IsEditMode && (Code != _loadedCode
            || ComponentName != _loadedName
            || SelectedBackend != _loadedBackend
            || !string.Equals(SelectedCustomPdk?.FilePath, _loadedPdkFilePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Prefills this editor from <paramref name="template"/>. Returns false (reason in
    /// <see cref="StatusText"/>) when the template's PDK is not among the custom-PDK choices —
    /// callers must then NOT show the window (a half-initialized session would appear).
    /// </summary>
    public bool LoadForEdit(ComponentTemplate template)
    {
        var match = PdkChoices.FirstOrDefault(c => !c.IsNewPdk && c.Pdk?.Name == template.PdkSource);
        if (match is null)
        {
            StatusText = $"Cannot edit '{template.Name}': its PDK '{template.PdkSource}' is not a custom PDK.";
            return false;
        }

        ComponentName = template.Name;

        var backend = template.RawCodeBackend == "nazca" ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        var code = template.RawCode ?? string.Empty;
        if (string.IsNullOrEmpty(code))
        {
            var synthesized = SynthesizeCodeFromReference(template);
            if (synthesized is { } s)
            {
                code = s.Code;
                backend = s.Backend;
                StatusText = "Loaded the foundry definition as editable code — adjust and save to fork it.";
            }
            else
            {
                StatusText = "No stored code for this component — enter code to edit.";
            }
        }
        SelectedBackend = backend;
        Code = code;
        SelectedPdkChoice = match;

        _editingOriginalName = template.Name;
        _loadedSMatrixDraft = template.SourceDraft?.SMatrix;
        _editOriginalPdkFilePath = match.Pdk!.FilePath;
        _editOriginalPdkName = match.Pdk.Name;
        _editOriginalProcessName = match.Pdk.Process?.Name;
        _loadedName = ComponentName;
        _loadedCode = Code;
        _loadedBackend = SelectedBackend;
        _loadedPdkFilePath = SelectedCustomPdk?.FilePath;
        IsEditMode = true;
        return true;
    }

    /// <summary>
    /// Prefills this editor from a BUNDLED component's template without forking its PDK yet —
    /// the fork is only created when "Save changes" runs (<see cref="HasPendingBundledFork"/>).
    /// Returns false, leaving no trace on disk, when the bundled PDK declares no fabrication
    /// process or the template cannot be loaded.
    /// </summary>
    public bool LoadForEditBundled(ComponentTemplate template, string bundledFilePath, ProcessDefinition? process)
    {
        if (process is null)
        {
            StatusText = $"Cannot edit '{template.Name}': its PDK '{template.PdkSource}' declares no fabrication process.";
            return false;
        }

        var forkTargetPath = _store.ResolveNamedPath(template.PdkSource);
        var forkChoice = PdkChoice.For(new UserPdkInfo(template.PdkSource, forkTargetPath, process));
        _pdkChoices.Insert(0, forkChoice);
        OnPropertyChanged(nameof(PdkChoices));

        if (!LoadForEdit(template))
        {
            _pdkChoices.Remove(forkChoice);
            OnPropertyChanged(nameof(PdkChoices));
            return false;
        }

        _pendingForkSourcePath = bundledFilePath;
        _pendingForkTargetPath = forkTargetPath;
        if (string.IsNullOrEmpty(StatusText))
            StatusText = "Editing a built-in component — \"Save changes\" creates your own editable copy of its PDK.";
        return true;
    }

    /// <summary>
    /// Turns a foundry component's function reference into equivalent editable code, so a
    /// bundled component opens with a runnable definition instead of a blank editor.
    /// gdsfactory-native PDKs register their cells in the PDK registry, NOT as module
    /// attributes ("cspdk.sin300.coupler_straight()" raises AttributeError), so the code uses
    /// the same import + PDK.activate() + gf.get_component() pattern as the canvas preview.
    /// </summary>
    private static (string Code, GeometryBackend Backend)? SynthesizeCodeFromReference(ComponentTemplate t)
    {
        if (!string.IsNullOrWhiteSpace(t.GdsFactoryFunction))
        {
            // Bare (dotless) cell names have no PDK module to activate — resolve them
            // against whatever PDK the render script activates by default.
            var code = GdsFactoryPreviewCode.For(t.GdsFactoryFunction)
                ?? $"import gdsfactory as gf\ncomponent = gf.get_component('{t.GdsFactoryFunction}')\n";
            return (code, GeometryBackend.GdsFactory);
        }
        if (!string.IsNullOrWhiteSpace(t.NazcaFunctionName))
        {
            var module = t.NazcaModuleName;
            var call = string.IsNullOrWhiteSpace(module) ? t.NazcaFunctionName : $"{module}.{t.NazcaFunctionName}";
            var import = string.IsNullOrWhiteSpace(module) ? "import nazca as nd" : $"import {module.Split('.')[0]}";
            return ($"{import}\ncomponent = {call}()", GeometryBackend.Nazca);
        }
        return null;
    }
}

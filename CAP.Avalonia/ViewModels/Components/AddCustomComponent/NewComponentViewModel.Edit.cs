using System.Linq;
using CAP.Avalonia.Services.AddCustomComponent;
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

    /// <summary>
    /// The PDK file path (or, lacking one, the PDK name) this edit session was loaded from.
    /// Together with <see cref="EditingOriginalName"/> this identifies which on-disk component
    /// is being edited, independent of any in-progress rename — used by the main window to key
    /// its open-editor-window dictionary so a second "Edit…" click on the same component
    /// activates the existing window instead of opening a duplicate (task-2 dedup).
    /// </summary>
    public string? EditOriginalPdkKey => _editOriginalPdkFilePath ?? _editOriginalPdkName;

    /// <summary>The component's name at the time <see cref="LoadForEdit"/> was called.</summary>
    public string? EditingOriginalName => _editingOriginalName;

    private string? _loadedName;
    private string? _loadedCode;

    /// <summary>
    /// True when the user changed the name or code since <see cref="LoadForEdit"/> (or the last
    /// <see cref="RefreshFromFreshEdit"/>). The main window's editor dedup consults this: a
    /// stale-but-clean editor is refreshed with the current on-disk state on a second ✏ click,
    /// while unsaved user input is never thrown away.
    /// </summary>
    public bool HasUnsavedEditChanges =>
        IsEditMode && (Code != _loadedCode || ComponentName != _loadedName);

    /// <summary>
    /// Prefills this editor from <paramref name="template"/>. Returns false (leaving
    /// <see cref="IsEditMode"/> off and the reason in <see cref="StatusText"/>) when the
    /// template's PDK has no matching entry in this VM's custom-PDK choices — callers must then
    /// NOT show the window, otherwise a half-initialized "New Component" session appears.
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
        _editOriginalPdkFilePath = match.Pdk!.FilePath;
        _editOriginalPdkName = match.Pdk.Name;
        _editOriginalProcessName = match.Pdk.Process?.Name;
        _loadedName = ComponentName;
        _loadedCode = Code;
        IsEditMode = true;
        return true;
    }

    /// <summary>
    /// Prefills this editor from a BUNDLED component's template without forking its PDK yet.
    /// The fork target (the user copy of the whole PDK in user-pdks) is offered as the selected
    /// PDK choice; the copy itself is only created when "Save changes" runs
    /// (<see cref="HasPendingBundledFork"/>). Returns false — leaving no trace on disk — when
    /// the bundled PDK declares no fabrication process or the template cannot be loaded.
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
    /// Adopts the freshly loaded template state of <paramref name="fresh"/> — a new VM that just
    /// ran <see cref="LoadForEdit"/> for the same component — into this already-open editor.
    /// Called by the main window's dedup when this editor has no unsaved user input
    /// (<see cref="HasUnsavedEditChanges"/> false), so a second ✏ click shows the current
    /// on-disk state instead of a stale snapshot whose Save would silently overwrite newer
    /// changes. Setting <see cref="NewComponentViewModel.Code"/> also invalidates the preview.
    /// </summary>
    public void RefreshFromFreshEdit(NewComponentViewModel fresh)
    {
        ComponentName = fresh.ComponentName;
        SelectedBackend = fresh.SelectedBackend;
        Code = fresh.Code;
        StatusText = fresh.StatusText;
        _editingOriginalName = fresh._editingOriginalName;
        _loadedName = fresh._loadedName;
        _loadedCode = fresh._loadedCode;
    }

    /// <summary>
    /// Turns a foundry component's function reference into equivalent editable code, so a bundled
    /// component opens with a visible, runnable definition instead of a blank editor.
    /// </summary>
    private static (string Code, GeometryBackend Backend)? SynthesizeCodeFromReference(ComponentTemplate t)
    {
        if (!string.IsNullOrWhiteSpace(t.GdsFactoryFunction))
        {
            var top = t.GdsFactoryFunction.Split('.')[0];
            return ($"import {top}\ncomponent = {t.GdsFactoryFunction}()", GeometryBackend.GdsFactory);
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

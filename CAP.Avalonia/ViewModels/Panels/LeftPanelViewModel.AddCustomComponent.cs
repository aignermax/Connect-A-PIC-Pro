using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    [RelayCommand]
    private async Task OpenNewComponent()
    {
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        await ShowNewComponentWindowAsync(NewComponentWindowLauncher.BuildViewModel(_addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(), RegisterSavedCustomComponent));
    }

    /// <summary>
    /// Raised with the fresh template after a saved definition replaced its library template.
    /// Wired to <see cref="FileOperationsViewModel.RefreshInstancesFromTemplate"/> so the saved
    /// S-matrix takes effect type-wide, including already-placed instances.
    /// </summary>
    public Action<ComponentTemplate>? TemplateDefinitionSaved { get; set; }

    public void RegisterSavedCustomComponent(PdkComponentDraft draft, string pdkName, string filePath, bool savedViaBundledFork = false)
    {
        // A fork-on-save file is the user's copy of the whole bundled PDK and replaces (shadows)
        // the bundled entry. Only the explicit fork flow may shadow — a save that merely SHARES
        // a bundled PDK's name must not.
        if (savedViaBundledFork && TryShadowBundledPdkWithSavedFork(pdkName, filePath))
        {
            NotifyTemplateDefinitionSaved(pdkName, draft.Name);
            return;
        }

        // Defensive guard: never register a second library entry under a loaded bundled PDK's
        // name — the UI blocks creating such PDKs, so reaching this means a stale/foreign file.
        if (PdkManager.LoadedPdks.Any(p => p.IsBundled && p.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase)))
        {
            _errorConsole?.LogError(
                $"Component '{draft.Name}' was saved to '{filePath}', but PDK '{pdkName}' collides with a " +
                "built-in PDK's name and was not added to the library. Rename the PDK and save again.");
            return;
        }

        CustomComponentLibraryRegistrar.Register(draft, pdkName, filePath, AllTemplates, Categories, PdkManager, _preferencesService, _pdkLoader, _loadedPdkDrafts, ReapplyActiveProcessAfterPdkChange, FilterComponents);
        NotifyTemplateDefinitionSaved(pdkName, draft.Name);
    }

    private void NotifyTemplateDefinitionSaved(string pdkName, string componentName)
    {
        if (TemplateDefinitionSaved is null)
            return;
        var fresh = AllTemplates.FirstOrDefault(t =>
            string.Equals(t.PdkSource, pdkName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, componentName, StringComparison.OrdinalIgnoreCase));
        if (fresh != null)
            TemplateDefinitionSaved(fresh);
    }

    internal void RemoveMigratedLibraryTemplate(string oldPdkName, string componentName)
    {
        var stale = AllTemplates.FirstOrDefault(t =>
            t.PdkSource == oldPdkName &&
            string.Equals(t.Name, componentName, System.StringComparison.OrdinalIgnoreCase));
        if (stale is null)
            return;

        AllTemplates.Remove(stale);
        if (!AllTemplates.Any(t => t.Category == stale.Category))
            Categories.Remove(stale.Category);

        var oldPdk = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == oldPdkName);
        if (oldPdk?.FilePath is { } path)
        {
            var normalized = Path.GetFullPath(path);
            _loadedPdkDrafts
                .FirstOrDefault(d => d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized)
                ?.Components.RemoveAll(c => string.Equals(c.Name, componentName, System.StringComparison.OrdinalIgnoreCase));
        }

        FilterComponents();
    }

    /// <summary>
    /// Whether the component editor applies: the template's PDK must be loaded
    /// and either bundled (edits fork on save) or file-backed. A design-scoped
    /// (GDS-imported) PDK — non-bundled with no file — has nowhere to save
    /// edits to, so its components are not editable.
    /// </summary>
    public bool CanEditTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.Any(p => p.Name == template.PdkSource && (p.IsBundled || p.FilePath != null));

    /// <summary>
    /// Whether the ✕ (delete / Restore Original) applies: the PDK must be a loaded non-bundled
    /// FILE-BACKED PDK (design-scoped GDS imports live in the .lun, not in a deletable file)
    /// AND the component must diverge from the bundled original — on a fork, untouched
    /// components have nothing to delete or restore.
    /// </summary>
    public bool CanDeleteTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource) is { IsBundled: false, FilePath: not null }
        && ComponentDivergesFromBundledOriginal(template);

    [RelayCommand]
    private async Task EditCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanEditTemplate(template)) return;
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);

        var vm = NewComponentWindowLauncher.BuildViewModel(
            _addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(),
            RegisterSavedCustomComponent, RemoveMigratedLibraryTemplate);

        // A bundled component opens with a DEFERRED fork: nothing is written to disk until
        // "Save changes" creates the user's copy of the PDK; closing without saving leaves no trace.
        var loaded = pdkInfo is { IsBundled: true, FilePath: not null }
            ? vm.LoadForEditBundled(template, pdkInfo.FilePath, ResolvePdkProcess(pdkInfo))
            : vm.LoadForEdit(template);
        if (!loaded)
        {
            // Never show a half-initialized window — surface LoadForEdit's reason instead.
            // The dropped view model still holds the backend picker's registry
            // subscription; release it or it keeps probing availability forever.
            vm.Dispose();
            _errorConsole?.LogError($"Cannot edit component '{template.Name}': {vm.StatusText}");
            UpdateStatus?.Invoke(vm.StatusText);
            return;
        }
        await ShowNewComponentWindowAsync(vm);
    }

    /// <summary>
    /// The fabrication process a PDK declares — from the loaded draft when available,
    /// otherwise read from its file.
    /// </summary>
    private ProcessDefinition? ResolvePdkProcess(PdkInfoViewModel pdkInfo)
    {
        if (pdkInfo.FilePath is null)
            return null;

        var normalized = Path.GetFullPath(pdkInfo.FilePath);
        var draft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized);
        if (draft?.Process is { } process)
            return process;

        try
        {
            return _pdkLoader.LoadFromFile(pdkInfo.FilePath).Process;
        }
        catch (Exception ex)
        {
            // An unreadable file is NOT "declares no fabrication process" — log the real cause.
            _errorConsole?.LogError($"Could not read PDK '{pdkInfo.Name}' at '{pdkInfo.FilePath}': {ex.Message}", ex);
            return null;
        }
    }
}

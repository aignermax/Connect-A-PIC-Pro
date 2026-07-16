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

    public void RegisterSavedCustomComponent(PdkComponentDraft draft, string pdkName, string filePath, bool savedViaBundledFork = false)
    {
        // A save that executed the deferred fork-on-save: the saved file is the user's copy of
        // the whole bundled PDK and replaces (shadows) the bundled entry instead of being
        // registered next to it. Only that explicit fork flow may shadow — a save that merely
        // SHARES a bundled PDK's name must not (PR #742 review, finding 1).
        if (savedViaBundledFork && TryShadowBundledPdkWithSavedFork(pdkName, filePath))
            return;

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

    public bool CanEditTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.Any(p => p.Name == template.PdkSource);

    /// <summary>
    /// Whether the ✕ (delete / Restore Original) applies to <paramref name="template"/>: its PDK
    /// must be a loaded non-bundled PDK AND the component must actually diverge from the bundled
    /// original — on a fork, untouched components are identical to the foundry truth and have
    /// nothing to delete or restore (field-test fix, PR #742).
    /// </summary>
    public bool CanDeleteTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource) is { IsBundled: false }
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

        // A bundled component opens read-only-sourced with a DEFERRED fork: nothing is written
        // to disk until "Save changes" actually creates the user's copy of the PDK
        // (fork-on-save; closing without saving leaves no trace).
        var loaded = pdkInfo is { IsBundled: true, FilePath: not null }
            ? vm.LoadForEditBundled(template, pdkInfo.FilePath, ResolvePdkProcess(pdkInfo))
            : vm.LoadForEdit(template);
        if (!loaded)
        {
            // No half-initialized "New Component" window when the edit session cannot be set up
            // (PR #742 review, finding 4) — surface LoadForEdit's reason instead.
            _errorConsole?.LogError($"Cannot edit component '{template.Name}': {vm.StatusText}");
            UpdateStatus?.Invoke(vm.StatusText);
            return;
        }
        await ShowNewComponentWindowAsync(vm);
    }

    /// <summary>
    /// The fabrication process a PDK declares — from the already-loaded draft when available,
    /// otherwise read from its file (a bundled PDK registered without a loaded draft, e.g. in
    /// tests). The deferred fork-on-save needs it to describe the fork target.
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
            // An unreadable/corrupt file is NOT "declares no fabrication process" — log the
            // real cause so the user-facing misdiagnosis can be traced (PR #742 review,
            // finding 9).
            _errorConsole?.LogError($"Could not read PDK '{pdkInfo.Name}' at '{pdkInfo.FilePath}': {ex.Message}", ex);
            return null;
        }
    }
}

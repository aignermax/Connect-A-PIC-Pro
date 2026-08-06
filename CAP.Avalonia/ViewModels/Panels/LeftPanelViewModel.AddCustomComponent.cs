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

        // While a batch scope is open, the expensive per-registration tail (process
        // re-application, full list re-filter, preferences write) is deferred to the
        // scope's dispose; the catalog itself (AllTemplates/Categories) still updates per call.
        Action reapply = ReapplyActiveProcessAfterPdkChange;
        Action filter = FilterComponents;
        if (_batchRegistrationDepth > 0)
        {
            _batchRegistrationRefreshPending = true;
            reapply = filter = static () => { };
        }
        CustomComponentLibraryRegistrar.Register(draft, pdkName, filePath, AllTemplates, Categories, PdkManager, _preferencesService, _pdkLoader, _loadedPdkDrafts, reapply, filter);
        NotifyTemplateDefinitionSaved(pdkName, draft.Name);
    }

    private int _batchRegistrationDepth;
    private bool _batchRegistrationRefreshPending;

    /// <summary>
    /// Defers the per-registration library refresh (process re-application, filtered-list
    /// rebuild and the preferences disk write inside it) until the returned scope is
    /// disposed, where it runs exactly once. Wrap bulk registrations in this scope —
    /// e.g. a GDS import registering hundreds of drafts — because per call the refresh
    /// re-sorts and re-publishes the whole filtered list and rewrites the preferences
    /// file on the UI thread. Scopes are ref-counted rather than rejected when nested:
    /// two composing bulk callers both just mean "defer until the outermost scope
    /// closes", so throwing would turn a harmless composition into a UI-thread crash.
    /// UI-thread only, like registration itself.
    /// </summary>
    public IDisposable BeginBatchRegistration()
    {
        _batchRegistrationDepth++;
        return new BatchRegistrationScope(this);
    }

    private void EndBatchRegistration()
    {
        _batchRegistrationDepth--;
        if (_batchRegistrationDepth > 0 || !_batchRegistrationRefreshPending)
            return;

        _batchRegistrationRefreshPending = false;
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }

    /// <summary>Closes its batch level exactly once — extra Dispose calls must not unbalance the depth counter.</summary>
    private sealed class BatchRegistrationScope : IDisposable
    {
        private LeftPanelViewModel? _owner;

        public BatchRegistrationScope(LeftPanelViewModel owner) => _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            owner?.EndBatchRegistration();
        }
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

    public bool CanEditTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.Any(p => p.Name == template.PdkSource);

    /// <summary>
    /// Whether the ✕ (delete / Restore Original) applies: the PDK must be a loaded non-bundled
    /// PDK AND the component must diverge from the bundled original — on a fork, untouched
    /// components have nothing to delete or restore.
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

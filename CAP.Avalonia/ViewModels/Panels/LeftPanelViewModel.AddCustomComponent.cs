using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// "New Component" assistant entry points for <see cref="LeftPanelViewModel"/> — opening it for a
/// brand-new component (issue #656) and opening it prefilled to edit an existing custom one
/// (issue #656 follow-up, task 6). Split out purely to keep <c>LeftPanelViewModel.cs</c> under
/// the project's line-count limit; still one partial class, one cohesive feature area.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>Opens the "New Component" window (issue #656); see <see cref="NewComponentWindowLauncher"/>.</summary>
    [RelayCommand]
    private async Task OpenNewComponent()
    {
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        await ShowNewComponentWindowAsync(NewComponentWindowLauncher.BuildViewModel(_addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(), RegisterSavedCustomComponent));
    }

    /// <summary>Registers a saved custom component into the library; see <see cref="CustomComponentLibraryRegistrar"/>.</summary>
    public void RegisterSavedCustomComponent(PdkComponentDraft draft, string pdkName, string filePath) =>
        CustomComponentLibraryRegistrar.Register(draft, pdkName, filePath, AllTemplates, Categories, PdkManager, _preferencesService, _pdkLoader, _loadedPdkDrafts, ReapplyActiveProcessAfterPdkChange, FilterComponents);

    /// <summary>
    /// True when <paramref name="template"/> belongs to any currently-loaded PDK, so the component
    /// editor may open for it. Bundled (Foundry) PDKs are editable too now: editing one forks its
    /// PDK into the writable user store first (<see cref="ForkBundledPdkForEdit"/>), so the shipped
    /// read-only copy is never touched. A stale reference to no loaded PDK is not editable.
    /// </summary>
    public bool CanEditTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.Any(p => p.Name == template.PdkSource);

    /// <summary>
    /// True when <paramref name="template"/> may be deleted — only components of a NON-bundled
    /// (user) PDK. A shipped bundled component cannot be removed (you can delete your own fork of
    /// it instead); this is the mirror of <see cref="CanEditTemplate"/>'s wider edit gate.
    /// </summary>
    public bool CanDeleteTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource) is { IsBundled: false };

    /// <summary>
    /// Opens the component editor prefilled for <paramref name="template"/>. For a bundled
    /// component the PDK is first forked into the editable user store and the library entry
    /// swapped to that fork, so edits land in a writable copy (the shipped original stays intact).
    /// </summary>
    [RelayCommand]
    private async Task EditCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanEditTemplate(template)) return;
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        if (pdkInfo is { IsBundled: true, FilePath: not null })
        {
            var forked = ForkBundledPdkForEdit(pdkInfo, template);
            if (forked is null) return; // fork failed — leave the read-only bundled copy untouched
            template = forked;
        }

        var vm = NewComponentWindowLauncher.BuildViewModel(
            _addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(), RegisterSavedCustomComponent);
        vm.LoadForEdit(template);
        await ShowNewComponentWindowAsync(vm);
    }

    /// <summary>
    /// Forks the bundled PDK behind <paramref name="template"/> into the user store, replaces the
    /// bundled library entry with the editable user copy (so there is no duplicate), and returns
    /// the matching template from the fork. Returns null if the store isn't wired or the forked
    /// component can't be located.
    /// </summary>
    private ComponentTemplate? ForkBundledPdkForEdit(PdkInfoViewModel bundled, ComponentTemplate template)
    {
        var store = _addCustomComponentDeps?.UserPdkStore;
        if (store is null || bundled.FilePath is null)
            return null;

        var forkPath = store.ForkBundledPdk(bundled.FilePath, bundled.Name);

        // Swap the bundled entry for the editable fork: drop the bundled templates + draft + manager
        // row, then load the fork as a user PDK (registers its templates + row under the same name).
        RemoveTemplatesForPdk(bundled.Name);
        _loadedPdkDrafts.RemoveAll(d => string.Equals(d.FilePath, bundled.FilePath, System.StringComparison.OrdinalIgnoreCase));
        var bundledRow = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == bundled.Name && p.IsBundled);
        if (bundledRow != null)
            PdkManager.LoadedPdks.Remove(bundledRow);

        TryReloadUserPdk(forkPath);
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();

        return AllTemplates.FirstOrDefault(t =>
            string.Equals(t.Name, template.Name, System.StringComparison.OrdinalIgnoreCase)
            && t.PdkSource == bundled.Name);
    }
}

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
    /// Takes a freshly created (possibly still component-less) custom PDK file into the loaded
    /// set immediately (issue #734, "Duplicate as custom PDK"): loads its draft, registers it
    /// with the PDK manager, persists its path for the next start, and re-applies the active
    /// process lock — so a value-compatible duplicate of a foundry process appears enabled at
    /// once instead of only after its first component is saved. Returns the loaded draft (or
    /// the already-loaded one when the file was registered before).
    /// </summary>
    public PdkDraft? RegisterCreatedCustomPdk(string filePath)
    {
        if (PdkManager.IsPdkLoaded(filePath))
            return GetLoadedPdkDrafts().FirstOrDefault(d => d.FilePath == filePath);

        // Edit-tolerant loader — the same one UserPdkStore reads its own files with — so a
        // fresh user PDK (which may lack Nazca origin offsets) still loads.
        var draft = _pdkLoader.LoadFromFileForEditing(filePath);
        _loadedPdkDrafts.Add(draft);
        PdkManager.RegisterPdk(draft.Name, filePath, false, draft.Components.Count);
        _preferencesService.AddUserPdkPath(filePath);
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return draft;
    }

    /// <summary>
    /// True when <paramref name="template"/> belongs to a currently-loaded, non-bundled PDK —
    /// the only components the "New Component" assistant is allowed to edit in place. Foundry
    /// (bundled) PDKs are read-only, so a template whose <see cref="ComponentTemplate.PdkSource"/>
    /// resolves to a bundled entry (or to none at all — e.g. a stale reference) is not editable.
    /// Re-derives the answer from the live <see cref="PdkManager"/> registry rather than trusting
    /// <see cref="ComponentTemplate.IsCustom"/> alone, so it stays correct even if that flag was
    /// never set (defensive default).
    /// </summary>
    public bool CanEditTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource) is { IsBundled: false };

    /// <summary>
    /// Opens the "New Component" assistant prefilled to edit an existing custom component
    /// (issue #656 follow-up, task 6). No-ops for a bundled/Foundry template — see
    /// <see cref="CanEditTemplate"/> — since those are read-only.
    /// </summary>
    [RelayCommand]
    private async Task EditCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanEditTemplate(template)) return;
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        var vm = NewComponentWindowLauncher.BuildViewModel(
            _addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(), RegisterSavedCustomComponent);
        vm.LoadForEdit(template);
        await ShowNewComponentWindowAsync(vm);
    }
}

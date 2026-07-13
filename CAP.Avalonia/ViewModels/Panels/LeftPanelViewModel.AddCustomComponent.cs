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

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

    public void RegisterSavedCustomComponent(PdkComponentDraft draft, string pdkName, string filePath) =>
        CustomComponentLibraryRegistrar.Register(draft, pdkName, filePath, AllTemplates, Categories, PdkManager, _preferencesService, _pdkLoader, _loadedPdkDrafts, ReapplyActiveProcessAfterPdkChange, FilterComponents);

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

    public bool CanDeleteTemplate(ComponentTemplate template) =>
        PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource) is { IsBundled: false };

    [RelayCommand]
    private async Task EditCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanEditTemplate(template)) return;
        if (ShowNewComponentWindowAsync is null || _addCustomComponentDeps is null) return;

        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        if (pdkInfo is { IsBundled: true, FilePath: not null })
        {
            var forked = ForkBundledPdkForEdit(pdkInfo, template);
            if (forked is null) return;
            template = forked;
        }

        var vm = NewComponentWindowLauncher.BuildViewModel(
            _addCustomComponentDeps, _pdkLoader, GetLoadedPdkDrafts(),
            RegisterSavedCustomComponent, RemoveMigratedLibraryTemplate);
        vm.LoadForEdit(template);
        await ShowNewComponentWindowAsync(vm);
    }

    private ComponentTemplate? ForkBundledPdkForEdit(PdkInfoViewModel bundled, ComponentTemplate template)
    {
        var store = _addCustomComponentDeps?.UserPdkStore;
        if (store is null || bundled.FilePath is null)
            return null;

        var forkPath = store.ForkBundledPdk(bundled.FilePath, bundled.Name);

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

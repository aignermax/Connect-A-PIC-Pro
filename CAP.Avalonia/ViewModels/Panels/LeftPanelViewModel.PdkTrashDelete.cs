using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    internal bool UnregisterPdk(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p =>
            p.FilePath != null && Path.GetFullPath(p.FilePath) == normalizedPath);
        if (pdkInfo is null || pdkInfo.IsBundled)
            return false;

        RemoveTemplatesForPdk(pdkInfo.Name);

        var draft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalizedPath);
        if (draft != null)
            _loadedPdkDrafts.Remove(draft);

        PdkManager.UnloadPdkCommand.Execute(pdkInfo);
        _preferencesService.RemoveUserPdkPath(filePath);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
        return true;
    }

    private void RemoveTemplatesForPdk(string pdkName)
    {
        var templatesToRemove = AllTemplates.Where(t => t.PdkSource == pdkName).ToList();
        foreach (var template in templatesToRemove)
            AllTemplates.Remove(template);

        foreach (var category in templatesToRemove.Select(t => t.Category).Distinct())
        {
            if (!AllTemplates.Any(t => t.Category == category))
                Categories.Remove(category);
        }
    }

    [RelayCommand]
    private void RemoveCustomComponent(ComponentTemplate? template)
    {
        if (template is null || !CanDeleteTemplate(template))
            return;

        var pdkInfo = PdkManager.LoadedPdks.FirstOrDefault(p => p.Name == template.PdkSource);
        var userPdkStore = _addCustomComponentDeps?.UserPdkStore;
        if (pdkInfo?.FilePath is null || userPdkStore is null)
            return;

        string? result;
        try
        {
            result = userPdkStore.RemoveComponent(pdkInfo.FilePath, template.Name);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError(
                $"Failed to delete component '{template.Name}' from PDK '{pdkInfo.Name}': {ex.Message}", ex);
            return;
        }
        if (result is null)
            return;

        AllTemplates.Remove(template);
        if (!AllTemplates.Any(t => t.Category == template.Category))
            Categories.Remove(template.Category);

        var normalizedPath = Path.GetFullPath(pdkInfo.FilePath);
        var draft = _loadedPdkDrafts.FirstOrDefault(d =>
            d.FilePath != null && Path.GetFullPath(d.FilePath) == normalizedPath);
        draft?.Components.RemoveAll(c => string.Equals(c.Name, template.Name, StringComparison.OrdinalIgnoreCase));

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }
}

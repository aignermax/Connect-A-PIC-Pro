using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

public static class CustomComponentLibraryRegistrar
{
    public static void Register(
        PdkComponentDraft draft, string pdkName, string filePath,
        ObservableCollection<ComponentTemplate> allTemplates,
        ObservableCollection<string> categories,
        PdkManagerViewModel pdkManager,
        UserPreferencesService preferencesService,
        PdkLoader pdkLoader,
        List<PdkDraft> loadedPdkDrafts,
        Action reapplyActiveProcess,
        Action filterComponents)
    {
        var template = PdkTemplateConverter.ConvertToTemplate(draft, pdkName, null);
        template.IsCustom = true;
        allTemplates.Add(template);
        if (!categories.Contains(template.Category))
            categories.Add(template.Category);

        if (!pdkManager.IsPdkLoaded(filePath))
        {
            if (File.Exists(filePath))
                loadedPdkDrafts.Add(pdkLoader.LoadFromFileForEditing(filePath));
            pdkManager.RegisterPdk(pdkName, filePath, false, 1);
            preferencesService.AddUserPdkPath(filePath);
        }

        reapplyActiveProcess();
        filterComponents();
    }
}

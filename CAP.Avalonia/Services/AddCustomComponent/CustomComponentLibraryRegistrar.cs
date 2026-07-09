using System;
using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Registers a user-authored component's saved <see cref="PdkComponentDraft"/> into the
/// component library (issue #656): converts it to a <see cref="ComponentTemplate"/>, adds it
/// (and, if new, its category), and — the first time this PDK file is seen — registers it
/// with the PDK manager and the user's persisted PDK path list. Stateless by design (a static
/// method over its inputs) so <c>LeftPanelViewModel.RegisterSavedCustomComponent</c> stays a
/// thin, always-available pass-through — this logic needs none of the "add custom component"
/// feature's optional collaborators (geometry extractor, FDTD, user-PDK store).
/// </summary>
public static class CustomComponentLibraryRegistrar
{
    /// <summary>Mirrors <c>LeftPanelViewModel.LoadPdkFromJsonFileAsync</c>'s registration pattern for a single component.</summary>
    public static void Register(
        PdkComponentDraft draft, string pdkName, string filePath,
        ObservableCollection<ComponentTemplate> allTemplates,
        ObservableCollection<string> categories,
        PdkManagerViewModel pdkManager,
        UserPreferencesService preferencesService,
        Action filterComponents)
    {
        var template = PdkTemplateConverter.ConvertToTemplate(draft, pdkName, null);
        allTemplates.Add(template);
        if (!categories.Contains(template.Category))
            categories.Add(template.Category);

        if (!pdkManager.IsPdkLoaded(filePath))
        {
            pdkManager.RegisterPdk(pdkName, filePath, false, 1);
            preferencesService.AddUserPdkPath(filePath);
        }

        filterComponents();
    }
}

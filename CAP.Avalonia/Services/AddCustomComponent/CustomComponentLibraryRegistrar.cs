using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Registers a user-authored component's saved <see cref="PdkComponentDraft"/> into the
/// component library (issue #656): converts it to a <see cref="ComponentTemplate"/>, adds it
/// (and, if new, its category), and — the first time this PDK file is seen — takes the whole
/// user-PDK draft into the loaded set exactly like a normal PDK load (so its process
/// fingerprint participates in single-process grouping and the active-process lock can govern
/// its visibility, issue #570), registers it with the PDK manager, and records its path.
/// Stateless by design (a static method over its inputs) so
/// <c>LeftPanelViewModel.RegisterSavedCustomComponent</c> stays a thin, always-available
/// pass-through — this logic needs none of the "add custom component" feature's optional
/// collaborators (geometry extractor, FDTD, user-PDK store).
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
        PdkLoader pdkLoader,
        List<PdkDraft> loadedPdkDrafts,
        Action reapplyActiveProcess,
        Action filterComponents)
    {
        var template = PdkTemplateConverter.ConvertToTemplate(draft, pdkName, null);
        // A saved custom component always lands in a user PDK — editable via the library's
        // "Edit…" action (issue #656 follow-up, task 6).
        template.IsCustom = true;
        allTemplates.Add(template);
        if (!categories.Contains(template.Category))
            categories.Add(template.Category);

        if (!pdkManager.IsPdkLoaded(filePath))
        {
            // Take the user PDK into the loaded set like a normal load (#570). Use the
            // edit-tolerant loader — the same one UserPdkStore reads its own files with —
            // so a freshly-saved user PDK (which may lack a Nazca origin offset) still loads.
            if (File.Exists(filePath))
                loadedPdkDrafts.Add(pdkLoader.LoadFromFileForEditing(filePath));
            pdkManager.RegisterPdk(pdkName, filePath, false, 1);
            preferencesService.AddUserPdkPath(filePath);
        }

        // A component saved for a NON-active process must not escape the active process lock;
        // one saved for the active process must appear immediately. Re-applying the lock (a
        // no-op in Playground / no selection) enforces both, then the explicit re-filter covers
        // the unlocked case (issue #570).
        reapplyActiveProcess();
        filterComponents();
    }
}

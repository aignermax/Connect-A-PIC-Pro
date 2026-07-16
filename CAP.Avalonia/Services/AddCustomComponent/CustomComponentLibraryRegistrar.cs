using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        // An edit-save re-registers an existing component: the on-disk PDK already replaced the
        // old entry (UserPdkStore.AppendToExistingPdk), so the in-memory library must too —
        // otherwise "Show stored S-matrices" keeps resolving the STALE template (FirstOrDefault
        // over AllTemplates) and the library lists the component twice (field-test fix, PR #742).
        // PDK names are compared case-insensitively everywhere in the save flow (the name is
        // re-read from the PDK file on every save) — a casing difference must not skip the
        // stale-template replacement and resurrect the duplicate/stale bug.
        var stale = allTemplates
            .Where(t => string.Equals(t.PdkSource, pdkName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Name, draft.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in stale)
            allTemplates.Remove(old);

        allTemplates.Add(template);
        if (!categories.Contains(template.Category))
            categories.Add(template.Category);
        foreach (var oldCategory in stale.Select(t => t.Category).Distinct())
        {
            if (!allTemplates.Any(t => t.Category == oldCategory))
                categories.Remove(oldCategory);
        }

        if (!pdkManager.IsPdkLoaded(filePath))
        {
            if (File.Exists(filePath))
                loadedPdkDrafts.Add(pdkLoader.LoadFromFileForEditing(filePath));
            pdkManager.RegisterPdk(pdkName, filePath, false, 1);
            preferencesService.AddUserPdkPath(filePath);
        }
        else
        {
            // Already-loaded PDK (the usual edit-save): mirror the on-disk replacement in the
            // cached draft too, so divergence checks and later edit sessions see the new state.
            var normalized = Path.GetFullPath(filePath);
            var cachedDraft = loadedPdkDrafts.FirstOrDefault(d =>
                d.FilePath != null && Path.GetFullPath(d.FilePath) == normalized);
            if (cachedDraft != null)
            {
                cachedDraft.Components.RemoveAll(c => string.Equals(c.Name, draft.Name, StringComparison.OrdinalIgnoreCase));
                cachedDraft.Components.Add(draft);
            }
        }

        reapplyActiveProcess();
        filterComponents();
    }
}

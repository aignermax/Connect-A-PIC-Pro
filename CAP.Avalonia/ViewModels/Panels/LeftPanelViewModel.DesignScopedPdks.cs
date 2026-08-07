using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Design-scoped PDK registration (issue #830): GDS-imported component sets
/// live in the open .lun design, not in a global user-PDK file. They register
/// here as in-memory PDKs — visible in the library panel while the design is
/// open, removed when it closes — recognizable by a null file path in
/// <see cref="PdkManager"/> (nothing on disk to edit, unload-persist or fork).
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>Backend name of GDS-imported raw-code components (Python nd.load_gds snippets).</summary>
    private const string DesignScopedPdkBackend = "nazca";

    /// <summary>
    /// Registers a design-scoped PDK: an in-memory draft (process-agnostic, so
    /// the set stays visible under a process lock — imported geometry carries
    /// no fabrication process) plus one library template per component. The
    /// registration refreshes the library once for the whole set.
    /// </summary>
    /// <param name="pdkName">Set name, also the templates' <c>PdkSource</c>.</param>
    /// <param name="drafts">Component drafts with the runtime .gds path already substituted into the raw code.</param>
    public void RegisterDesignScopedPdk(string pdkName, IReadOnlyList<PdkComponentDraft> drafts)
    {
        if (PdkManager.IsPdkNameLoaded(pdkName, null))
        {
            // A same-named global PDK is already loaded (e.g. a legacy import PDK the user
            // opened manually) — its templates already resolve the design's placements, and
            // a second registration would duplicate every entry.
            _errorConsole?.LogWarning(
                $"Imported component set '{pdkName}' was not registered: a PDK with this name is already loaded.");
            return;
        }

        _loadedPdkDrafts.Add(new PdkDraft
        {
            Name = pdkName,
            ProcessAgnostic = true,
            Backend = DesignScopedPdkBackend,
            Components = drafts.ToList(),
        });

        foreach (var draft in drafts)
        {
            var template = ConvertPdkComponentToTemplate(draft, pdkName, null);
            template.IsCustom = true;
            AllTemplates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
        }

        PdkManager.RegisterPdk(pdkName, null, false, drafts.Count);
        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }

    /// <summary>
    /// Removes a design-scoped PDK (templates, orphaned categories, in-memory
    /// draft, manager entry) when its design closes. A no-op for names that are
    /// NOT design-scoped — a same-named file-backed PDK must never be torn down
    /// by a design closing.
    /// </summary>
    public void RemoveDesignScopedPdk(string pdkName)
    {
        var managerEntry = PdkManager.LoadedPdks.FirstOrDefault(p =>
            !p.IsBundled && p.FilePath is null &&
            p.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase));
        if (managerEntry is null)
            return;

        var staleTemplates = AllTemplates
            .Where(t => t.PdkSource.Equals(pdkName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var template in staleTemplates)
            AllTemplates.Remove(template);
        foreach (var category in staleTemplates.Select(t => t.Category).Distinct().ToList())
        {
            if (!AllTemplates.Any(t => t.Category == category))
                Categories.Remove(category);
        }

        _loadedPdkDrafts.RemoveAll(d =>
            d.FilePath is null && d.Name.Equals(pdkName, StringComparison.OrdinalIgnoreCase));
        PdkManager.LoadedPdks.Remove(managerEntry);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }
}

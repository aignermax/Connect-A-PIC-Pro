using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.GdsImport.DesignScope;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Test host for the design-scoped GDS import (issue #830): a
/// <see cref="DesignScopedGdsComponentService"/> wired to throwaway library
/// collections — mirroring <c>LeftPanelViewModel</c>'s design-scope
/// registration — with a temp-directory .gds cache.
/// </summary>
public sealed class GdsDesignScopeTestHost : IDisposable
{
    /// <summary>Runtime library templates registered from the design scope.</summary>
    public ObservableCollection<ComponentTemplate> Templates { get; } = new();

    /// <summary>Runtime library categories registered from the design scope.</summary>
    public ObservableCollection<string> Categories { get; } = new();

    /// <summary>The throwaway PDK manager the design-scoped sets register into.</summary>
    public PdkManagerViewModel PdkManager { get; } = new();

    /// <summary>The in-memory drafts registered from the design scope.</summary>
    public List<PdkDraft> LoadedDrafts { get; } = new();

    /// <summary>Temp cache directory the embedded .gds bytes materialize into.</summary>
    public string GdsCacheDirectory { get; }

    /// <summary>The design-scope store under test, wired to this host's library state.</summary>
    public DesignScopedGdsComponentService Scope { get; }

    /// <summary>Creates the host with a fresh temp .gds cache directory.</summary>
    /// <param name="userPdkStore">Optional legacy-migration source store (temp-rooted in tests).</param>
    /// <param name="pdkLoader">Optional loader for legacy import-PDK files.</param>
    public GdsDesignScopeTestHost(UserPdkStore? userPdkStore = null, PdkLoader? pdkLoader = null)
    {
        GdsCacheDirectory = Path.Combine(
            Path.GetTempPath(), "lunima-test-gds-cache-" + Guid.NewGuid().ToString("N"));
        Scope = new DesignScopedGdsComponentService(
            RegisterPdk, RemovePdk, GdsCacheDirectory, userPdkStore, pdkLoader);
    }

    /// <summary>Creates an import service on this host's design scope.</summary>
    /// <param name="templateProvider">
    /// Known-component resolver source; defaults to this host's registered templates.
    /// </param>
    public GdsImportService CreateService(
        Func<IReadOnlyList<ComponentTemplate>>? templateProvider = null) =>
        new(Scope, templateProvider ?? (() => Templates.ToList()));

    private void RegisterPdk(string pdkName, IReadOnlyList<PdkComponentDraft> drafts)
    {
        LoadedDrafts.Add(new PdkDraft
        {
            Name = pdkName,
            ProcessAgnostic = true,
            Backend = "nazca",
            Components = drafts.ToList(),
        });
        foreach (var draft in drafts)
        {
            var template = PdkTemplateConverter.ConvertToTemplate(draft, pdkName, null);
            template.IsCustom = true;
            Templates.Add(template);
            if (!Categories.Contains(template.Category))
                Categories.Add(template.Category);
        }
        PdkManager.RegisterPdk(pdkName, null, false, drafts.Count);
    }

    private void RemovePdk(string pdkName)
    {
        for (var i = Templates.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Templates[i].PdkSource, pdkName, StringComparison.OrdinalIgnoreCase))
                Templates.RemoveAt(i);
        }
        LoadedDrafts.RemoveAll(d => string.Equals(d.Name, pdkName, StringComparison.OrdinalIgnoreCase));
        var entry = PdkManager.LoadedPdks.FirstOrDefault(p =>
            string.Equals(p.Name, pdkName, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
            PdkManager.LoadedPdks.Remove(entry);
    }

    /// <summary>Deletes the temp .gds cache directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(GdsCacheDirectory))
            Directory.Delete(GdsCacheDirectory, true);
    }
}

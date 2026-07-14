using System;
using System.Linq;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// One-step "Duplicate as custom PDK" for read-only bundled/foundry PDKs (issue #734): since the
/// PDK/process UX refinement (#733), a foundry PDK's process can no longer be edited in place —
/// the foundry JSON is the manufacturer's truth. To extend such a process (e.g. add a metal
/// cross-section for electrical routing, #682), this creates a NAMED custom PDK carrying a
/// value-identical deep copy of the foundry process, which the per-PDK process editor may then
/// modify freely. The source PDK's file is never read or written here — only its already-loaded
/// draft is copied — so the foundry JSON stays untouched by construction.
/// </summary>
public static class BundledPdkDuplicationService
{
    /// <summary>
    /// Backend recorded when the source PDK declares none — absent means Nazca by convention
    /// (see <see cref="PdkDraft.Backend"/>).
    /// </summary>
    private const string DefaultBackend = "nazca";

    /// <summary>
    /// Creates a new named custom PDK whose process is a deep copy of <paramref name="source"/>'s
    /// process, preserving the source's layout backend and routing cross-section so components
    /// saved into the duplicate export the same way. Returns the created file's path.
    /// </summary>
    /// <param name="store">The user-PDK store the new custom PDK is created in.</param>
    /// <param name="source">The loaded draft of the PDK to duplicate; must declare a process.</param>
    /// <param name="newPdkName">Display name for the new custom PDK.</param>
    /// <exception cref="ArgumentException"><paramref name="newPdkName"/> is empty/whitespace.</exception>
    /// <exception cref="InvalidOperationException">The source has no process, or a custom PDK
    /// with that name (or slugged file name) already exists.</exception>
    public static string Duplicate(UserPdkStore store, PdkDraft source, string newPdkName)
    {
        if (string.IsNullOrWhiteSpace(newPdkName))
            throw new ArgumentException("The new custom PDK needs a name.", nameof(newPdkName));

        if (source.Process is null)
            throw new InvalidOperationException(
                $"PDK '{source.Name}' declares no fabrication process, so there is nothing to duplicate.");

        var name = newPdkName.Trim();
        // Same display-name collision rule as the Create-Custom-PDK dialog (#732): checked
        // against stored display names, not just the slugged file name, so "My Lib" vs "My-Lib"
        // are not conflated. CreateNamedPdkWithProcess additionally refuses an existing file.
        if (store.ListCustomPdks().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A custom PDK named '{name}' already exists.");

        // Deep copy (never alias): the per-PDK editor opened on the duplicate must not be able
        // to mutate the still-loaded foundry draft's process object in memory.
        var processCopy = ProcessDefinitionCloner.Clone(source.Process);
        return store.CreateNamedPdkWithProcess(
            name, processCopy, source.Backend ?? DefaultBackend, source.GdsFactoryRoutingCrossSection);
    }
}

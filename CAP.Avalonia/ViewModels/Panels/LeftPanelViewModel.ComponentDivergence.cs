using System.Text.Json;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Divergence bookkeeping for fork PDKs: only components whose definition differs from the
/// bundled original get the inline delete/restore ✕ — an untouched fork component has nothing
/// to restore. Components of plain custom PDKs (no bundled original) always keep their ✕.
/// </summary>
public partial class LeftPanelViewModel
{
    // Both sides are re-serialized with the SAME options, so on-disk formatting
    // (indentation, property order) never causes a false divergence.
    private static readonly JsonSerializerOptions DraftComparisonOptions = new();

    /// <summary>
    /// True when there is something to delete or restore: the bundled original lacks the
    /// component (user-added → plain delete) or defines it differently (edited → restore).
    /// Uses the session-cached bundled draft; no file is parsed.
    /// </summary>
    internal bool ComponentDivergesFromBundledOriginal(ComponentTemplate template)
    {
        var bundled = GetBundledOriginDraft(template.PdkSource);
        if (bundled is null)
            return true; // no bundled original known: plain custom-delete semantics

        var counterpart = bundled.Components.FirstOrDefault(c =>
            string.Equals(c.Name, template.Name, StringComparison.OrdinalIgnoreCase));
        if (counterpart is null)
            return true; // added by the user: the original never had it

        if (template.SourceDraft is not { } current)
            return true; // definition unknown: keep the ✕ rather than strand the component

        return !DraftsAreEquivalent(current, counterpart);
    }

    private static bool DraftsAreEquivalent(PdkComponentDraft a, PdkComponentDraft b) =>
        ReferenceEquals(a, b)
        || JsonSerializer.Serialize(a, DraftComparisonOptions) == JsonSerializer.Serialize(b, DraftComparisonOptions);

    /// <summary>Called once per library change, never per hover/binding — keeps the divergence check off the hot path.</summary>
    private void RefreshTemplateDeletableFlags()
    {
        foreach (var template in AllTemplates)
            template.IsDeletable = CanDeleteTemplate(template);
    }
}

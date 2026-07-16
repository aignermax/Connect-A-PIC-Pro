using System.Text.Json;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Divergence bookkeeping for fork PDKs: on a user fork of a bundled PDK, only components whose
/// definition actually differs from the bundled original get the inline delete/restore ✕ — an
/// untouched fork component has nothing to restore, so its ✕ is hidden (the ✏ stays everywhere).
/// Components of plain custom PDKs (no bundled original) always keep their delete ✕.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>
    /// Serializer used for the structural draft comparison. Both sides are compared as
    /// deserialized <see cref="PdkComponentDraft"/> object graphs re-serialized with the SAME
    /// options, so on-disk formatting (indentation, comments, property order in hand-written
    /// JSON) never causes a false divergence.
    /// </summary>
    private static readonly JsonSerializerOptions DraftComparisonOptions = new();

    /// <summary>
    /// True when <paramref name="template"/>'s definition differs from the bundled original —
    /// i.e. there is something to delete or restore. Components of a PDK without a bundled
    /// original (plain custom PDKs) always diverge; on a fork, a component diverges when the
    /// bundled original lacks it (newly added → plain delete) or defines it differently
    /// (edited → Restore Original). Uses the session-cached bundled draft; no file is parsed.
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

    /// <summary>Structural equality of two component drafts via canonical re-serialization.</summary>
    private static bool DraftsAreEquivalent(PdkComponentDraft a, PdkComponentDraft b) =>
        ReferenceEquals(a, b)
        || JsonSerializer.Serialize(a, DraftComparisonOptions) == JsonSerializer.Serialize(b, DraftComparisonOptions);

    /// <summary>
    /// Recomputes <see cref="ComponentTemplate.IsDeletable"/> (the ✕ visibility) for every
    /// library template. Called once per library change from
    /// <see cref="ReapplyActiveProcessAfterPdkChange"/> — never per hover/binding, so the
    /// divergence check stays off the hot path.
    /// </summary>
    private void RefreshTemplateDeletableFlags()
    {
        foreach (var template in AllTemplates)
            template.IsDeletable = CanDeleteTemplate(template);
    }
}

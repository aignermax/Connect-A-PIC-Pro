using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Process;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Maps the active-process selection to/from its persisted form and infers it for legacy
/// files that predate single-process support (issue #570).
/// </summary>
public static class ActiveProcessResolver
{
    /// <summary>Serialises a selection, or null when none is set.</summary>
    public static ActiveProcessData? ToData(ActiveProcessSelection? sel) => sel == null ? null : new ActiveProcessData
    {
        DisplayName = sel.DisplayName,
        IsPlayground = sel.IsPlayground,
        CoreMaterial = sel.Fingerprint?.CoreMaterial,
        CoreThicknessNm = sel.Fingerprint?.CoreThicknessNm,
        Cladding = sel.Fingerprint?.Cladding,
        DesignWavelengthNm = sel.Fingerprint?.DesignWavelengthNm ?? 1550,
        ProcessName = sel.Fingerprint?.ProcessName,
        MemberPdkNames = sel.MemberPdkNames.ToList(),
    };

    /// <summary>Deserialises a persisted selection, or null.</summary>
    public static ActiveProcessSelection? FromData(ActiveProcessData? data)
    {
        if (data == null) return null;
        if (data.IsPlayground) return ActiveProcessSelection.Playground();
        var fp = new ProcessFingerprint(data.CoreMaterial, data.CoreThicknessNm, data.Cladding,
            data.DesignWavelengthNm, data.ProcessName);
        return new ActiveProcessSelection(data.DisplayName, fp, data.MemberPdkNames, IsPlayground: false);
    }

    /// <summary>
    /// Infers the active process for a legacy design from the PDK sources of its placed
    /// components. One matching group → that process; several → Playground + a warning;
    /// none → null (empty / built-ins only).
    /// </summary>
    /// <param name="componentPdkSources">PDK source name of every placed component.</param>
    /// <param name="catalog">The currently installed process groups.</param>
    /// <param name="warning">Set when the design falls back to Playground.</param>
    /// <param name="processAgnosticPdkNames">
    /// Names of loaded PDKs flagged process-agnostic (e.g. "Analysis Tools" — virtual
    /// analyzers and other tool libraries). These are excluded from the migration decision,
    /// just like built-ins, so a design using only a real process plus an analyzer migrates
    /// to that process instead of Playground (issue #570 final review).
    /// </param>
    public static ActiveProcessSelection? Migrate(
        IEnumerable<string?> componentPdkSources,
        IReadOnlyList<ProcessGroup> catalog,
        out string? warning,
        IReadOnlyCollection<string>? processAgnosticPdkNames = null)
    {
        warning = null;
        var pdkNames = componentPdkSources
            .Where(s => !SingleProcessPolicy.IsBuiltIn(s) &&
                        !(processAgnosticPdkNames?.Contains(s!, System.StringComparer.OrdinalIgnoreCase) ?? false))
            .Select(s => s!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pdkNames.Count == 0) return null;

        var matched = catalog
            .Where(g => g.MemberPdkNames.Any(m => pdkNames.Contains(m, System.StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 1)
            return ActiveProcessSelection.ForGroup(matched[0]);

        if (matched.Count == 0)
        {
            warning = "This design uses PDK(s) that are not currently available: " +
                $"{string.Join(", ", pdkNames)}. Opened in Playground — install the missing " +
                "PDK(s) or start a new design.";
            return ActiveProcessSelection.Playground();
        }

        warning = "This design contains components from multiple processes " +
            $"({string.Join(", ", matched.Select(g => g.DisplayName))}). Opened in Playground — " +
            "not manufacturable. Remove conflicting components or start a new design.";
        return ActiveProcessSelection.Playground();
    }
}

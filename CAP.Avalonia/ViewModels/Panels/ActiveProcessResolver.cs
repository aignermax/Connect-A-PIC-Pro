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
        DesignWavelengthNm = sel.Fingerprint?.DesignWavelengthNm ?? ProcessFingerprint.DefaultDesignWavelengthNm,
        ProcessName = sel.Fingerprint?.ProcessName,
        WidthToleranceNm = sel.Fingerprint?.Tolerances?.WidthSigmaNm,
        ThicknessToleranceNm = sel.Fingerprint?.Tolerances?.ThicknessSigmaNm,
        MemberPdkNames = sel.MemberPdkNames.ToList(),
    };

    /// <summary>Deserialises a persisted selection, or null.</summary>
    public static ActiveProcessSelection? FromData(ActiveProcessData? data)
    {
        if (data == null) return null;
        if (data.IsPlayground) return ActiveProcessSelection.Playground();
        var tolerances = data.WidthToleranceNm == null && data.ThicknessToleranceNm == null
            ? null
            : new ProcessTolerances(
                data.WidthToleranceNm ?? ProcessTolerances.DefaultWidthSigmaNm,
                data.ThicknessToleranceNm ?? ProcessTolerances.DefaultThicknessSigmaNm);
        var fp = new ProcessFingerprint(data.CoreMaterial, data.CoreThicknessNm, data.Cladding,
            data.DesignWavelengthNm, data.ProcessName, tolerances);
        return new ActiveProcessSelection(data.DisplayName, fp, data.MemberPdkNames, IsPlayground: false);
    }

    /// <summary>
    /// Re-anchors a stored (non-legacy) selection to the currently installed process
    /// catalog. A stored process is a snapshot of the save-time member-PDK list; matching
    /// against the live catalog (by fingerprint compatibility, falling back to member-name
    /// overlap for unspecified fingerprints) means newly installed compatible PDKs join the
    /// process, and designs whose PDKs are missing get an explicit warning instead of
    /// silently locking the library to nonexistent names.
    /// </summary>
    /// <param name="stored">The selection read from the design file. Playground passes through.</param>
    /// <param name="catalog">The currently installed process groups.</param>
    /// <param name="warning">Set when no installed PDK belongs to the stored process.</param>
    public static ActiveProcessSelection Revalidate(
        ActiveProcessSelection stored,
        IReadOnlyList<ProcessGroup> catalog,
        out string? warning)
    {
        warning = null;
        if (stored.IsPlayground) return stored;

        var match = catalog.FirstOrDefault(g =>
            (stored.Fingerprint is { IsSpecified: true } fp && g.Fingerprint.IsSpecified &&
             ProcessCompatibility.AreCompatible(g.Fingerprint, fp)) ||
            g.MemberPdkNames.Intersect(stored.MemberPdkNames, System.StringComparer.OrdinalIgnoreCase).Any());

        if (match != null)
            return ActiveProcessSelection.ForGroup(match);

        warning = $"This design is locked to the process '{stored.DisplayName}', but none of its " +
            $"PDK(s) ({string.Join(", ", stored.MemberPdkNames)}) are installed. Only tool " +
            "components are available — install the missing PDK(s) to edit the design.";
        return stored;
    }

    /// <summary>
    /// Derives the design-level default for a loaded design that carries no own process
    /// record but whose chiplets restored per-group bindings (issue #938): a single
    /// distinct chiplet process becomes the design default; several distinct processes
    /// yield Playground — the canvas genuinely mixes processes there, and the
    /// manufacturability information lives in the per-chiplet bindings. No warning is
    /// needed either way: the file fully describes the state, nothing was migrated.
    /// Returns null when no chiplet carries a real process binding.
    /// </summary>
    /// <param name="chipletBindings">Restored bindings of the top-level groups.</param>
    public static ActiveProcessSelection? FromChipletBindings(IEnumerable<ActiveProcessSelection?> chipletBindings)
    {
        var distinct = chipletBindings
            .Where(b => b is { IsPlayground: false })
            .GroupBy(b => b!.DisplayName, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0].First(),
            _ => ActiveProcessSelection.Playground(),
        };
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
            .Where(s => !SingleProcessPolicy.IsExempt(s, processAgnosticPdkNames))
            .Select(s => s!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pdkNames.Count == 0) return null;

        var matched = catalog
            .Where(g => g.MemberPdkNames.Any(m => pdkNames.Contains(m, System.StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 1)
        {
            // Partial coverage is not silent success: components from PDKs the matched
            // process does NOT cover (typically uninstalled PDKs) would otherwise sit on
            // a chip that now claims to be manufacturable under that process.
            var uncovered = pdkNames
                .Where(n => !matched[0].MemberPdkNames.Contains(n, System.StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (uncovered.Count > 0)
                warning = $"Locked to process '{matched[0].DisplayName}', but the design also " +
                    $"contains components from unavailable PDK(s): {string.Join(", ", uncovered)}. " +
                    "Those components are not covered by the process — remove them or install their PDK(s).";
            return ActiveProcessSelection.ForGroup(matched[0]);
        }

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

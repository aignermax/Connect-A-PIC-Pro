using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Single-fabrication-process enforcement for <see cref="LeftPanelViewModel"/> (issue #570):
/// deriving process fingerprints from loaded PDKs, applying/clearing the active-process PDK
/// lock, and re-applying it after a PDK is loaded or a custom component is saved. Split out
/// purely to keep <c>LeftPanelViewModel.cs</c> under the project's line-count limit; still one
/// partial class, one cohesive feature area.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>
    /// Process fingerprints of all loaded PDKs, for single-process grouping (#570).
    /// Excludes process-agnostic tool PDKs (e.g. "Analysis Tools") — they are not a
    /// fabrication process and must not appear as a selectable process in the catalog.
    /// </summary>
    public IReadOnlyList<PdkProcessEntry> GetLoadedPdkProcessEntries() =>
        _loadedPdkDrafts.Where(d => !d.ProcessAgnostic)
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d))).ToList();

    /// <summary>
    /// All currently loaded PDK drafts. The Fabrication Process details dialog reads the
    /// members' <c>process</c> blocks from here so it always reflects the live PDK state
    /// (issue #660) instead of keeping its own copy.
    /// </summary>
    public IReadOnlyList<PdkDraft> GetLoadedPdkDrafts() => _loadedPdkDrafts;

    /// <summary>
    /// Names of loaded PDKs flagged process-agnostic (e.g. "Analysis Tools" — virtual analyzers
    /// and other tool libraries). These stay usable regardless of the active fabrication process
    /// (issue #570).
    /// </summary>
    public IReadOnlyList<string> GetProcessAgnosticPdkNames() =>
        _loadedPdkDrafts.Where(d => d.ProcessAgnostic).Select(d => d.Name).ToList();

    /// <summary>
    /// Drives the library filter to the active process's PDKs (issue #570). A real (non-Playground)
    /// process locks the enabled set to its member PDKs plus any process-agnostic tool PDKs, and
    /// disallows manual toggling; Playground or no selection restores manual control and brings the
    /// user's own (persisted) enable selection back — the locked set is derived state and must
    /// never replace it.
    /// </summary>
    public void ApplyActiveProcess(ActiveProcessSelection? active, bool preserveMemberToggles = false)
    {
        _lastAppliedProcess = active;
        if (active is { IsPlayground: false })
        {
            // The lock flag must be set BEFORE ApplyProcessLock, whose FilterComponents →
            // SavePdkFilterState guard reads it.
            PdkManager.ManualTogglesEnabled = false;
            PdkManager.ApplyProcessLock(
                ResolveLiveMemberPdkNames(active).Concat(GetProcessAgnosticPdkNames()),
                preserveMemberToggles);
            FilterComponents();
        }
        else
        {
            PdkManager.ManualTogglesEnabled = true;
            PdkManager.ClearProcessLock();
            // Leaving a locked process: restore the user's persisted selection instead of
            // keeping the previous process's enable set (which would silently hide every
            // other PDK in Playground). RestorePdkFilterState already re-filters.
            RestorePdkFilterState();
            FilterComponents();
        }
    }

    /// <summary>
    /// Determines the runtime-allowed member PDK names for <paramref name="active"/> by value
    /// rather than trusting its persisted <see cref="ActiveProcessSelection.MemberPdkNames"/>
    /// snapshot. That snapshot is fixed at the moment the process was selected/saved with the
    /// design; a custom PDK registered afterward (e.g. a named custom PDK that adopts a
    /// value-compatible CornerStone-preset process) is physically the same process but is
    /// missing from the snapshot, so it would stay locked out forever.
    /// <para>
    /// Instead this allows exactly those currently loaded PDKs whose OWN process fingerprint is
    /// <see cref="ProcessCompatibility.AreCompatible"/> with the active process's fingerprint —
    /// a direct per-PDK comparison against the active fingerprint, never a group-representative
    /// match. That distinction matters because compatibility is deliberately non-transitive
    /// (thickness/wavelength are tolerance bands): a <see cref="ProcessCatalog"/> group only
    /// guarantees its members are pairwise-within-tolerance of each other, not that every member
    /// is within tolerance of the active process. Comparing each PDK directly guarantees no
    /// over-tolerance PDK is ever unlocked (issue #570).
    /// </para>
    /// Falls back to the snapshot when there is no fingerprint to match (legacy selections), so
    /// ordinary behavior is unchanged. The persisted snapshot itself is never mutated — this only
    /// affects the runtime lock computed here.
    /// <para>
    /// Internal (not private): besides the library-filter lock above, the placement/paste/AI-grid
    /// guards consult this same set (wired by <c>MainViewModel</c> into
    /// <c>CanvasInteractionViewModel.GetLiveMemberPdkNames</c> and
    /// <c>AiGridService.GetLiveMemberPdkNames</c>, plus the metal-spec providers), so the library
    /// filter and every placement surface always agree on membership. <c>UnitTests</c> has
    /// InternalsVisibleTo.
    /// </para>
    /// <para>
    /// Layer-stack check (issue #570 follow-up): the fingerprint alone (materials/thickness/
    /// wavelength) says nothing about GDS layer NUMBERS — a PDK whose "NITRIDE" layer was
    /// renumbered from 203 to 2030 is still fingerprint-compatible but would mix mismatched layer
    /// numbers into one chip, which is unmanufacturable. So a candidate must ALSO pass
    /// <see cref="ProcessLayerConsistency.LayersConsistent"/> against a reference process: among
    /// the currently-loaded PDK drafts whose name is in the snapshot <c>active.MemberPdkNames</c>
    /// and whose <c>Process</c> is set, this prefers a BUNDLED one (via <see cref="PdkManager"/>'s
    /// <c>LoadedPdks</c>, <see cref="PdkInfoViewModel.IsBundled"/>) — falling back to the first
    /// loaded one only when no bundled member is loaded. Bundled PDKs are read-only Foundry truth,
    /// so their layer numbering can never have drifted; a custom snapshot member may have been
    /// edited (e.g. its layers renumbered) after the process was saved, and if THAT edited draft
    /// were picked as reference merely because it happened to be loaded first, comparing every
    /// other candidate — including the real Foundry PDK itself — against its now-divergent layer
    /// stack would wrongly lock the Foundry out of its own process (LC-T3 review finding). If no
    /// snapshot member with a set process is loaded at all, the reference comparison is skipped
    /// and only the pairwise consistency below applies — layer checks may only ever make the
    /// member set STRICTER than plain fingerprint compatibility, never wider (issue #570).
    /// Deliberately NOT applied to <see cref="ProcessCatalog"/>
    /// grouping (still fingerprint-only there): the catalog only needs a coarse "these are roughly
    /// the same process" grouping for the UI, while this live lock must be strict — an intentional
    /// asymmetry, not an oversight.
    /// </para>
    /// <para>
    /// Beyond the reference comparison, accepted members must also be layers-consistent with EACH
    /// OTHER (pairwise): a layer name absent from the reference (e.g. two custom PDKs each adding
    /// a "METAL" layer the Foundry process doesn't define) passes the reference check for both,
    /// but if they disagree on its (Layer, Datatype) numbers they must not both be placeable on
    /// one chip. Candidates are considered bundled-first, then snapshot members, then load order,
    /// so the Foundry/original members always win such a conflict deterministically and a
    /// later-loaded conflicting PDK is the one locked out. All name comparisons are
    /// case-insensitive, matching <c>SingleProcessPolicy</c>/<c>PdkManagerViewModel</c>.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> ResolveLiveMemberPdkNames(ActiveProcessSelection active)
    {
        if (active.Fingerprint is not { IsSpecified: true } fingerprint)
            return active.MemberPdkNames;

        var loadedDrafts = GetLoadedPdkDrafts();
        var snapshotMembersWithProcess = loadedDrafts
            .Where(d => active.MemberPdkNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)
                        && d.Process != null)
            .ToList();
        var referenceProcess = (
            snapshotMembersWithProcess.FirstOrDefault(d => IsBundledPdkName(d.Name))
            ?? snapshotMembersWithProcess.FirstOrDefault()
        )?.Process;

        // Bundled first, then snapshot members, then load order (OrderBy is stable) — the
        // deterministic precedence for the pairwise conflict resolution documented above.
        var candidates = GetLoadedPdkProcessEntries()
            .Where(e => e.Fingerprint.IsSpecified && ProcessCompatibility.AreCompatible(e.Fingerprint, fingerprint))
            .OrderByDescending(e => IsBundledPdkName(e.PdkName))
            .ThenByDescending(e => active.MemberPdkNames.Contains(e.PdkName, StringComparer.OrdinalIgnoreCase));

        var accepted = new List<(string Name, ProcessDefinition? Process)>();
        foreach (var candidate in candidates)
        {
            var candidateProcess = loadedDrafts.FirstOrDefault(d =>
                string.Equals(d.Name, candidate.PdkName, StringComparison.OrdinalIgnoreCase))?.Process;
            if (!ProcessLayerConsistency.LayersConsistent(referenceProcess, candidateProcess))
                continue;
            if (accepted.Any(a => !ProcessLayerConsistency.LayersConsistent(a.Process, candidateProcess)))
                continue;
            accepted.Add((candidate.PdkName, candidateProcess));
        }

        return accepted.Select(a => a.Name).ToList();
    }

    /// <summary>
    /// True when a loaded PDK named <paramref name="name"/> is bundled (Foundry, read-only) per
    /// <see cref="PdkManager"/>'s live registry. Used to prefer a bundled PDK as the
    /// layer-consistency reference in <see cref="ResolveLiveMemberPdkNames"/> (LC-T3 review
    /// finding) — false for a name that isn't currently loaded at all.
    /// </summary>
    private bool IsBundledPdkName(string name) =>
        PdkManager.LoadedPdks.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { IsBundled: true };

    /// <summary>
    /// The most recently applied process selection. Re-applied when a PDK is loaded
    /// afterwards, so importing a PDK while a process is locked cannot slip foreign
    /// components into the library (issue #570).
    /// </summary>
    private ActiveProcessSelection? _lastAppliedProcess;

    /// <summary>Re-applies the current process lock after a PDK load/import.</summary>
    internal void ReapplyActiveProcessAfterPdkChange()
    {
        if (_lastAppliedProcess is { IsPlayground: false })
            ApplyActiveProcess(_lastAppliedProcess, preserveMemberToggles: true);
    }
}

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
    public void ApplyActiveProcess(ActiveProcessSelection? active)
    {
        _lastAppliedProcess = active;
        if (active is { IsPlayground: false })
        {
            // Order matters: the lock flag must be set BEFORE ApplyProcessLock — that call
            // triggers FilterComponents → SavePdkFilterState, whose guard reads the flag.
            // Reversed, the locked set would be persisted over the user's own selection.
            PdkManager.ManualTogglesEnabled = false;
            // Member + tool PDKs stay individually toggleable (library filtering);
            // only foreign-process PDKs get their checkbox locked.
            PdkManager.ApplyProcessLock(ResolveLiveMemberPdkNames(active).Concat(GetProcessAgnosticPdkNames()));
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
    /// <see cref="ProcessLayerConsistency.LayersConsistent"/> against a reference process: the
    /// first currently-loaded PDK draft whose name is in the snapshot <c>active.MemberPdkNames</c>
    /// and whose <c>Process</c> is set — i.e. the defining foundry/ur-PDK for this process, not
    /// just any value-compatible one. If that reference PDK isn't loaded, the layer check is
    /// skipped (fingerprint-only, unchanged behavior) — this method must never let an over-broad
    /// layer check narrow the set below what plain fingerprint compatibility already allowed, and
    /// it must never widen it either. Deliberately NOT applied to <see cref="ProcessCatalog"/>
    /// grouping (still fingerprint-only there): the catalog only needs a coarse "these are roughly
    /// the same process" grouping for the UI, while this live lock must be strict — an intentional
    /// asymmetry, not an oversight.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> ResolveLiveMemberPdkNames(ActiveProcessSelection active)
    {
        if (active.Fingerprint is not { IsSpecified: true } fingerprint)
            return active.MemberPdkNames;

        var loadedDrafts = GetLoadedPdkDrafts();
        var referenceProcess = loadedDrafts
            .FirstOrDefault(d => active.MemberPdkNames.Contains(d.Name) && d.Process != null)
            ?.Process;

        return GetLoadedPdkProcessEntries()
            .Where(e => e.Fingerprint.IsSpecified &&
                        ProcessCompatibility.AreCompatible(e.Fingerprint, fingerprint) &&
                        ProcessLayerConsistency.LayersConsistent(
                            referenceProcess,
                            loadedDrafts.FirstOrDefault(d => d.Name == e.PdkName)?.Process))
            .Select(e => e.PdkName)
            .ToList();
    }

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
            ApplyActiveProcess(_lastAppliedProcess);
    }
}

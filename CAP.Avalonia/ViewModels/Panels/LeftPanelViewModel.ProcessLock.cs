using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    public IReadOnlyList<PdkProcessEntry> GetLoadedPdkProcessEntries() =>
        _loadedPdkDrafts.Where(d => !d.ProcessAgnostic)
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d))).ToList();

    public IReadOnlyList<PdkDraft> GetLoadedPdkDrafts() => _loadedPdkDrafts;

    public IReadOnlyList<string> GetProcessAgnosticPdkNames() =>
        _loadedPdkDrafts.Where(d => d.ProcessAgnostic).Select(d => d.Name).ToList();

    public void ApplyActiveProcess(ActiveProcessSelection? active, bool preserveMemberToggles = false)
    {
        _lastAppliedProcess = active;
        if (active is { IsPlayground: false })
        {
            // ManualTogglesEnabled must be set BEFORE ApplyProcessLock, whose FilterComponents → SavePdkFilterState guard reads it.
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
            RestorePdkFilterState();
            FilterComponents();
        }
    }

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
    /// True when <paramref name="name"/> carries foundry authority for the layer-consistency
    /// reference: either a bundled PDK itself, or the user's fork shadowing one — the fork is a
    /// copy of the foundry PDK under the same name and inherits its reference role, so forking
    /// never demotes the foundry process below an unrelated custom PDK.
    /// </summary>
    private bool IsBundledPdkName(string name) =>
        PdkManager.LoadedPdks.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            is { IsBundled: true } or { ShadowsBundledPdk: true };

    private ActiveProcessSelection? _lastAppliedProcess;

    internal void ReapplyActiveProcessAfterPdkChange()
    {
        if (_lastAppliedProcess is { IsPlayground: false })
            ApplyActiveProcess(_lastAppliedProcess, preserveMemberToggles: true);
    }
}

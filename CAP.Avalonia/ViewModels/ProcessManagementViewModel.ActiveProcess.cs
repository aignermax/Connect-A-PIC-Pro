using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Wires the Fabrication Process details dialog to the design's active process
/// (issue #660, follow-up to #652). The dialog derives its state fresh from
/// <c>FileOperationsViewModel.ActiveProcess</c> and the loaded PDK drafts every
/// time it opens — it never keeps its own copy of "the current process".
/// </summary>
public partial class ProcessManagementViewModel
{
    /// <summary>Fingerprint of the active process (core, thickness, cladding, wavelength).</summary>
    [ObservableProperty]
    private string _fingerprintSummary = string.Empty;

    /// <summary>Comma-separated member PDKs of the active process.</summary>
    [ObservableProperty]
    private string _memberPdksText = string.Empty;

    /// <summary>True when the design is in Playground (no single process; drives the banner).</summary>
    [ObservableProperty]
    private bool _isPlaygroundState;

    /// <summary>
    /// Derives the dialog state from the design's active process selection and the loaded
    /// PDK drafts: a locked process shows its name, fingerprint, member PDKs and the merged
    /// <c>process</c> blocks of those members; Playground and "no process" show explicit
    /// explanations instead of an empty page.
    /// </summary>
    public void ShowActiveProcess(ActiveProcessSelection? active, IReadOnlyList<PdkDraft> loadedPdks)
    {
        ResetState();
        SetAvailablePresets(loadedPdks);

        if (active == null)
        {
            StatusText = LocalizationService.Instance.Translate("ProcessMgmt.Status.NoProcessSelected");
            return;
        }

        if (active.IsPlayground)
        {
            IsPlaygroundState = true;
            ProcessName = active.DisplayName;
            StatusText = LocalizationService.Instance.Translate("ProcessMgmt.Status.PlaygroundBanner");
            return;
        }

        ShowLockedProcess(active, loadedPdks);
    }

    private void ShowLockedProcess(ActiveProcessSelection active, IReadOnlyList<PdkDraft> loadedPdks)
    {
        ProcessName = active.DisplayName;
        FingerprintSummary = FormatFingerprint(active.Fingerprint);
        MemberPdksText = active.MemberPdkNames.Count > 0
            ? string.Join(", ", active.MemberPdkNames)
            : LocalizationService.Instance.Translate("ProcessMgmt.MemberPdks.NoneLoaded");

        // Keep the member drafts so edits (e.g. a metal cross-section, #682) can be persisted
        // back to their PDK JSON via SaveProcess.
        _memberDrafts = MemberDrafts(active, loadedPdks);

        var definitions = _memberDrafts.Select(d => d.Process).Where(p => p != null).Select(p => p!).ToList();
        foreach (var definition in definitions)
            Merge(definition);

        // Only rows merged from the member PDKs' OWN process definitions above are "owned" by
        // this process — a later ImportFromPdk (reference import) must not be able to sneak
        // unrelated rows into SaveProcess's output (issue #686 review, Finding 2).
        MarkAllRowsOwned();

        // Merge() may adopt the PDK-declared process name; the dialog reflects the
        // active selection, so its display name always wins.
        ProcessName = active.DisplayName;
        HasProcess = true;

        StatusText = definitions.Count > 0
            ? string.Format(
                LocalizationService.Instance.Translate("ProcessMgmt.Status.ActiveMerged"), definitions.Count)
            : LocalizationService.Instance.Translate("ProcessMgmt.Status.ActiveNoDetail");
    }

    private static IReadOnlyList<PdkDraft> MemberDrafts(
        ActiveProcessSelection active, IReadOnlyList<PdkDraft> loadedPdks) =>
        active.MemberPdkNames
            .Select(name => MetalTraceStyleResolver.FindByName(loadedPdks, name, d => d.Name))
            .Where(d => d != null)
            .Select(d => d!)
            .ToList();

    private static string FormatFingerprint(ProcessFingerprint? fp)
    {
        if (fp == null)
            return LocalizationService.Instance.Translate("ProcessMgmt.Fingerprint.None");

        var core = fp.CoreMaterial ?? "?";
        var thickness = fp.CoreThicknessNm.HasValue
            ? fp.CoreThicknessNm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " nm"
            : "? nm";
        var cladding = fp.Cladding ?? "?";
        return string.Format(
            LocalizationService.Instance.Translate("ProcessMgmt.Fingerprint.Summary"),
            core, thickness, cladding, fp.DesignWavelengthNm);
    }

    private void ResetState()
    {
        ProcessName = string.Empty;
        FingerprintSummary = string.Empty;
        MemberPdksText = string.Empty;
        IsPlaygroundState = false;
        HasProcess = false;
        Layers.Clear();
        Xsections.Clear();
        Materials.Clear();
        _memberDrafts = new List<PdkDraft>();
        MarkAllRowsOwned();
    }
}

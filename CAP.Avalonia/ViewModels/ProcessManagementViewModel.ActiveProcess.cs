using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        _loadedPdks = loadedPdks;
        SetAvailablePresets(loadedPdks);

        if (active == null)
        {
            StatusText = "No process selected yet. Pick one at startup or via File \u2192 New Design; " +
                         "this dialog then shows that process's layer stack, materials and design rules.";
            return;
        }

        if (active.IsPlayground)
        {
            IsPlaygroundState = true;
            ProcessName = active.DisplayName;
            StatusText = "Playground \u2014 the design has no single fabrication process and is not " +
                         "manufacturable. Pick a real process via File \u2192 New Design to see its details here.";
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
            : "(none loaded)";

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

        // Preselect the preset dropdown on the active process so the current selection is
        // visible instead of an empty picker (issue #726). SetProperty skips the generated
        // OnSelectedPresetChanged, so the merged multi-member state above is not clobbered
        // by a single-preset reload.
        var activePreset = AvailablePresets.FirstOrDefault(
            p => active.MemberPdkNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        SetProperty(ref _selectedPreset, activePreset, nameof(SelectedPreset));

        StatusText = definitions.Count > 0
            ? $"Active process for this design. Layer stack, cross-sections and materials merged from " +
              $"{definitions.Count} member PDK(s)."
            : "Active process for this design. Its member PDKs declare no detailed process block \u2014 " +
              "showing the fingerprint only; data can be imported or entered below.";
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
            return "No fingerprint declared by the member PDKs.";

        var core = fp.CoreMaterial ?? "?";
        var thickness = fp.CoreThicknessNm.HasValue
            ? fp.CoreThicknessNm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " nm"
            : "? nm";
        var cladding = fp.Cladding ?? "?";
        return $"Core {core} \u00b7 {thickness} \u00b7 cladding {cladding} \u00b7 \u03bb {fp.DesignWavelengthNm} nm";
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
        // Clear via SetProperty so no preset Load is triggered by the generated setter.
        SetProperty(ref _selectedPreset, null, nameof(SelectedPreset));
        MarkAllRowsOwned();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Lets the editor change the DESIGN's process selection, not just the process definition
/// (issue #726): before this, picking a preset only filled the local editor collections and
/// was discarded on close — <c>FileOperationsViewModel.ActiveProcess</c> was never written,
/// so a reopened dialog showed the old state and the design process could not be switched.
/// </summary>
public partial class ProcessManagementViewModel
{
    /// <summary>All loaded PDK drafts, captured on open; used to build the process catalog
    /// so switching groups compatible PDKs exactly like the New-Design picker does.</summary>
    private IReadOnlyList<PdkDraft> _loadedPdks = new List<PdkDraft>();

    /// <summary>
    /// Applies a process selection to the design. Wired by the UI layer to
    /// <c>FileOperationsViewModel.SetActiveProcess</c>; null (tests/headless without wiring)
    /// disables switching with an explanatory status instead of failing silently.
    /// </summary>
    public Action<ActiveProcessSelection>? ApplyActiveProcess { get; set; }

    /// <summary>
    /// Supplies the number of components currently placed on the canvas. Switching the design
    /// process is only allowed while the canvas is empty — placed components carry S-matrices
    /// of the old process and would silently become process-inconsistent (#570: one design,
    /// one process). Null (tests/headless) counts as an empty canvas.
    /// </summary>
    public Func<int>? PlacedComponentCountProvider { get; set; }

    /// <summary>
    /// Locks the design to the process of the preset picked in the dropdown (issue #726).
    /// Only allowed while no components are placed; the selection is built through
    /// <see cref="ProcessCatalog.BuildGroups"/> over all loaded PDKs, so compatible PDKs
    /// join the process exactly as they would in the New-Design picker.
    /// </summary>
    [RelayCommand]
    private void SetAsDesignProcess()
    {
        if (SelectedPreset == null)
        {
            StatusText = "Pick a process in the preset dropdown first, then set it as the design process.";
            return;
        }
        if (ApplyActiveProcess == null)
        {
            StatusText = "Switching the design process is not available in this context.";
            return;
        }

        var placedCount = PlacedComponentCountProvider?.Invoke() ?? 0;
        if (placedCount > 0)
        {
            StatusText = $"Cannot switch the design process: {placedCount} component(s) are placed. " +
                         "Their S-matrices belong to the current process (one design = one process). " +
                         "Remove all components or start a new design first.";
            return;
        }

        var selection = BuildSelectionFor(SelectedPreset);
        ApplyActiveProcess(selection);
        StatusText = $"'{selection.DisplayName}' is now this design's fabrication process. " +
                     "The library is filtered to its member PDK(s).";
    }

    /// <summary>
    /// Builds the design's <see cref="ActiveProcessSelection"/> for a preset PDK via the same
    /// catalog grouping the New-Design picker uses, so the resulting process (display name,
    /// fingerprint, compatible member PDKs) is identical no matter where it was chosen.
    /// Falls back to a singleton selection when the preset is not in the catalog (e.g. it is
    /// flagged process-agnostic).
    /// </summary>
    private ActiveProcessSelection BuildSelectionFor(PdkDraft preset)
    {
        var entries = _loadedPdks
            .Where(d => !d.ProcessAgnostic)
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d)));
        var group = ProcessCatalog.BuildGroups(entries).FirstOrDefault(
            g => g.MemberPdkNames.Contains(preset.Name, StringComparer.OrdinalIgnoreCase));

        return group != null
            ? ActiveProcessSelection.ForGroup(group)
            : new ActiveProcessSelection(
                preset.Name, ProcessFingerprintFactory.From(preset),
                new List<string> { preset.Name }, IsPlayground: false);
    }
}

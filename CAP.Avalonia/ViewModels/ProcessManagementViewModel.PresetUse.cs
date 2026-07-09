using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CAP.Avalonia.ViewModels.Process;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// "Use preset" semantics (issue #696): picking a preset makes it the DESIGN's active
/// fabrication process (persisted in the .lun file, restored on close/reopen) instead of
/// merely filling the editor. Field edits become design-specific overrides on top of the
/// preset — the PDK file on disk only ever changes via the explicit "Save to PDK file…".
/// </summary>
public partial class ProcessManagementViewModel
{
    /// <summary>Loaded PDK drafts captured when the dialog opened; resolves preset definitions.</summary>
    private IReadOnlyList<PdkDraft> _loadedPdks = new List<PdkDraft>();

    /// <summary>The preset's unmodified process definition — the baseline overrides are diffed against.</summary>
    private ProcessDefinition? _presetDefinition;

    /// <summary>Name of the PDK whose process is in use as a preset; null when none is.</summary>
    private string? _presetPdkName;

    /// <summary>JSON snapshot of the last committed overrides, to avoid redundant commits.</summary>
    private string _committedOverridesJson = "[]";

    /// <summary>Banner text: "Using preset 'X'" plus the override count. Empty without a preset.</summary>
    [ObservableProperty]
    private string _presetStateText = string.Empty;

    /// <summary>True while the design's process comes from a preset (drives the banner).</summary>
    [ObservableProperty]
    private bool _isPresetInUse;

    /// <summary>
    /// Makes the picked preset the design's active process. Wired by the UI layer to
    /// <c>FileOperationsViewModel.SetActiveProcess</c> + <c>SetActiveProcessPreset</c>,
    /// so the pick marks the design dirty and survives save/load and close/reopen (#696).
    /// </summary>
    public Action<ActiveProcessSelection, string>? UseAsDesignProcess { get; set; }

    /// <summary>
    /// Persists the design's current overrides on top of the preset. Wired by the UI layer
    /// to <c>FileOperationsViewModel.SetActiveProcessPreset</c>.
    /// </summary>
    public Action<string, IReadOnlyList<ProcessPropertyOverrideData>>? CommitOverrides { get; set; }

    /// <summary>
    /// Picking an entry in "Use preset ▾" USES that PDK's fabrication process for this design
    /// (issue #696): the selection is pushed to the design and the editor re-derives from it.
    /// Editing a field afterwards creates design-only overrides — it never silently modifies
    /// the preset/PDK itself.
    /// </summary>
    partial void OnSelectedPresetChanged(PdkDraft? value)
    {
        if (value == null)
            return;

        var fingerprint = ProcessFingerprintFactory.From(value);
        var displayName = fingerprint.ProcessName ?? value.Name;
        var selection = new ActiveProcessSelection(
            displayName, fingerprint, new List<string> { value.Name }, IsPlayground: false);

        UseAsDesignProcess?.Invoke(selection, value.Name);
        // Re-derive the dialog from the new selection so the editor shows the preset's values.
        // In headless tests the picked draft may not be in the open-time PDK list — include it.
        var pdks = _loadedPdks.Contains(value) ? _loadedPdks : _loadedPdks.Append(value).ToList();
        ShowActiveProcess(selection, pdks, value.Name, presetOverrides: null);
        StatusText = $"Now using preset '{displayName}' as this design's fabrication process " +
                     "(stored with the design). Edit a field to override it for this design only; " +
                     "the PDK file itself changes only via 'Save to PDK file…'.";
    }

    /// <summary>
    /// Re-diffs the editor state against the preset baseline: updates the banner
    /// ("Using preset 'X' — N properties overridden") and commits changed overrides to the
    /// design. Called by the window after field edits (focus loss) and on close; no-op when
    /// the process does not come from a preset.
    /// </summary>
    public void RefreshOverrideSummary()
    {
        if (_presetDefinition == null || _presetPdkName == null)
            return;

        var overrides = ProcessOverrideDiff.Diff(_presetDefinition, ToProcess());
        UpdatePresetStateText(overrides.Count);

        var json = JsonSerializer.Serialize(overrides);
        if (json == _committedOverridesJson)
            return;
        _committedOverridesJson = json;
        CommitOverrides?.Invoke(_presetPdkName, overrides);
    }

    /// <summary>
    /// Restores the preset context when the dialog opens on a design whose process was picked
    /// as a preset: applies the stored overrides to the displayed rows and shows the banner.
    /// </summary>
    private void InitialisePresetState(string? presetPdkName, IReadOnlyList<ProcessPropertyOverrideData>? overrides)
    {
        if (presetPdkName == null)
            return;

        var draft = MetalTraceStyleResolver.FindByName(_loadedPdks, presetPdkName, d => d.Name);
        if (draft?.Process == null)
            return;

        _presetPdkName = presetPdkName;
        // Deep-clone the baseline: the loaded draft's definition must never serve as both
        // the diff baseline AND the edited state, and typing in the editor must not mutate
        // the in-memory PDK preset (USE, not edit — issue #696).
        _presetDefinition = ProcessOverrideDiff.Clone(draft.Process);
        var stored = overrides ?? Array.Empty<ProcessPropertyOverrideData>();
        _committedOverridesJson = JsonSerializer.Serialize(stored);

        // The editor shows the EFFECTIVE process (preset + design overrides) on detached
        // copies; the design's display name wins over the definition's, as in ShowLockedProcess.
        var displayName = ProcessName;
        Load(ProcessOverrideDiff.Apply(_presetDefinition, stored));
        ProcessName = displayName;
        UpdatePresetStateText(stored.Count);
    }

    /// <summary>Clears the preset context; called whenever the dialog state is re-derived.</summary>
    private void ResetPresetState()
    {
        _presetDefinition = null;
        _presetPdkName = null;
        _committedOverridesJson = "[]";
        PresetStateText = string.Empty;
        IsPresetInUse = false;
    }

    private void UpdatePresetStateText(int overrideCount)
    {
        IsPresetInUse = true;
        var noun = overrideCount == 1 ? "property" : "properties";
        PresetStateText = overrideCount == 0
            ? $"Using preset '{_presetPdkName}' — unchanged. Edits become design-only overrides."
            : $"Using preset '{_presetPdkName}' — {overrideCount} {noun} overridden for this design " +
              "(stored in the design file; the PDK file is unchanged).";
    }
}

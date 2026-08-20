using CAP_Core.Components.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Register-designation half of <see cref="TruthTableViewModel"/>: the "Register
/// (state element)" toggle in the Truth Table panel binds the persisted
/// <see cref="TruthTablePinAssignment.IsRegister"/> flag of the selected group
/// (issue #1098, UI slice of #1086). A register-designated gate's outputs hold
/// their committed value while the network settles; only an explicit step samples
/// its inputs and commits them (D-semantics), and a feedback cycle is legal exactly
/// when it passes through such a gate. The toggle writes through to the group's
/// persisted assignment as soon as one exists; before the first extraction it is
/// pure intent the extraction persists alongside the pin roles.
/// </summary>
public partial class TruthTableViewModel
{
    /// <summary>
    /// True when the selected group is designated a behavioral register state
    /// element. Mirrored from the persisted assignment on selection; user edits
    /// write back into it (or ride into the assignment the extraction creates).
    /// </summary>
    [ObservableProperty]
    private bool _isRegister;

    /// <summary>Writes the toggle through to the group's persisted assignment, when one exists.</summary>
    partial void OnIsRegisterChanged(bool value)
    {
        if (_revertingPinCheck)
            return;
        var assignment = _group?.TruthTablePinAssignment;
        if (assignment != null)
            assignment.IsRegister = value;
    }
}

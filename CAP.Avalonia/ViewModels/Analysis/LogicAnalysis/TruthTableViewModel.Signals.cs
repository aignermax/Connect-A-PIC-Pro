using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Signal-name half of <see cref="TruthTableViewModel"/> (issue #1033): the editable
/// signal field on each checked input row writes
/// <see cref="TruthTablePinAssignment.InputSignalNames"/> on the selected group.
/// Names are trimmed on write; an empty-after-trim field means "no signal" and
/// removes the pin's entry — a map that empties collapses to null so legacy .lun
/// files stay byte-clean. Signal names only ever live on input pins: a pin that
/// loses its input role (unchecked, or checked as output/bias) drops its entry.
/// </summary>
public partial class TruthTableViewModel
{
    /// <summary>
    /// Applies one signal-field edit: the trimmed name becomes the pin's persisted
    /// signal, or — empty — removes the pin from the map. Silent while the group
    /// carries no persisted assignment (nothing to attach a name to).
    /// </summary>
    private void ApplySignalNameEdit(PinSelectionViewModel pin)
    {
        var assignment = _group?.TruthTablePinAssignment;
        if (assignment == null)
            return;

        var name = pin.SignalName.Trim();
        if (name.Length == 0 || !pin.IsChecked)
        {
            RemoveSignalNameEntry(pin.PinName);
            return;
        }
        (assignment.InputSignalNames ??= new Dictionary<string, string>())[pin.PinName] = name;
    }

    /// <summary>
    /// Drops the pin's signal identity together with its input role: the map entry
    /// goes and the field clears, so text and map always mirror each other.
    /// </summary>
    private void RevokeSignalName(PinSelectionViewModel pin)
    {
        pin.SignalName = "";
        RemoveSignalNameEntry(pin.PinName);
    }

    /// <summary>Removes one pin's entry; an emptied map collapses to null.</summary>
    private void RemoveSignalNameEntry(string pinName)
    {
        var assignment = _group?.TruthTablePinAssignment;
        var map = assignment?.InputSignalNames;
        if (map == null || !map.Remove(pinName))
            return;
        if (map.Count == 0)
            assignment!.InputSignalNames = null;
    }

    /// <summary>Restores the persisted signal names into the freshly ticked input rows.</summary>
    private void PrefillSignalNames(TruthTablePinAssignment saved)
    {
        if (saved.InputSignalNames == null)
            return;
        foreach (var (pinName, signal) in saved.InputSignalNames)
        {
            var pin = InputPins.FirstOrDefault(p => p.PinName == pinName);
            if (pin != null)
                pin.SignalName = signal;
        }
    }

    /// <summary>Pushes the panel-level visibility flag onto the input rows.</summary>
    partial void OnSignalNamesVisibleChanged(bool value)
    {
        foreach (var pin in InputPins)
            pin.SignalEditingVisible = value;
    }
}

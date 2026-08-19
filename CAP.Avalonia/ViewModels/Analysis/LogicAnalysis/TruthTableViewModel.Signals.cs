using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Signal-name half of <see cref="TruthTableViewModel"/>: the editable signal field
/// on each checked input row writes <see cref="TruthTablePinAssignment.InputSignalNames"/>,
/// on each checked output row <see cref="TruthTablePinAssignment.OutputSignalNames"/>,
/// both on the selected group. Names are trimmed on write; an empty-after-trim field
/// means "no signal" and removes the pin's entry — a map that empties collapses to
/// null so legacy .lun files stay byte-clean. Signal names only ever live on input
/// and output pins: a pin that loses its role (unchecked, or checked into another
/// role) drops its entry.
/// </summary>
public partial class TruthTableViewModel
{
    /// <summary>
    /// Applies one signal-field edit: the trimmed name becomes the pin's persisted
    /// signal, or — empty — removes the pin from its map. Silent while the group
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
            RemoveSignalNameEntry(pin);
            return;
        }
        if (InputPins.Contains(pin))
            (assignment.InputSignalNames ??= new Dictionary<string, string>())[pin.PinName] = name;
        else
            (assignment.OutputSignalNames ??= new Dictionary<string, string>())[pin.PinName] = name;
    }

    /// <summary>
    /// Drops the pin's signal identity together with its role: the map entry
    /// goes and the field clears, so text and map always mirror each other.
    /// </summary>
    private void RevokeSignalName(PinSelectionViewModel pin)
    {
        pin.SignalName = "";
        RemoveSignalNameEntry(pin);
    }

    /// <summary>Removes one pin's entry from its role's map; an emptied map collapses to null.</summary>
    private void RemoveSignalNameEntry(PinSelectionViewModel pin)
    {
        var assignment = _group?.TruthTablePinAssignment;
        if (assignment == null)
            return;
        var isInput = InputPins.Contains(pin);
        var map = isInput ? assignment.InputSignalNames : assignment.OutputSignalNames;
        if (map == null || !map.Remove(pin.PinName))
            return;
        if (map.Count != 0)
            return;
        if (isInput)
            assignment.InputSignalNames = null;
        else
            assignment.OutputSignalNames = null;
    }

    /// <summary>Restores the persisted signal names into the freshly ticked input and output rows.</summary>
    private void PrefillSignalNames(TruthTablePinAssignment saved)
    {
        PrefillSignalNames(saved.InputSignalNames, InputPins);
        PrefillSignalNames(saved.OutputSignalNames, OutputPins);
    }

    /// <summary>Restores one persisted signal-name map into the matching rows.</summary>
    private static void PrefillSignalNames(
        IReadOnlyDictionary<string, string>? names, IEnumerable<PinSelectionViewModel> pins)
    {
        if (names == null)
            return;
        foreach (var (pinName, signal) in names)
        {
            var pin = pins.FirstOrDefault(p => p.PinName == pinName);
            if (pin != null)
                pin.SignalName = signal;
        }
    }

    /// <summary>Pushes the panel-level visibility flag onto the input and output rows.</summary>
    partial void OnSignalNamesVisibleChanged(bool value)
    {
        foreach (var pin in InputPins.Concat(OutputPins))
            pin.SignalEditingVisible = value;
    }
}

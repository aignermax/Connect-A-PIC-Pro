using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Bus-view half of <see cref="LogicPanelViewModel"/> (issue #1068, NAND game rung 5):
/// turns the flat <see cref="Inputs"/>/<see cref="Outputs"/> lists into grouped rows —
/// indexed signal families (<c>A0</c>–<c>A3</c>) collapse into bus header rows showing
/// the decimal value, everything else stays a plain row. Purely display-level: the
/// network, the timeline, and the canvas badges keep reading the flat lists.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>The Inputs list as grouped display rows: bus headers and plain toggles.</summary>
    public ObservableCollection<LogicInputRowViewModel> InputRows { get; } = new();

    /// <summary>The Outputs list as grouped display rows: bus headers and plain indicators.</summary>
    public ObservableCollection<LogicOutputRowViewModel> OutputRows { get; } = new();

    /// <summary>
    /// Rebuilds both row collections from the current flat lists — call after
    /// <see cref="Inputs"/> and <see cref="Outputs"/> were refilled.
    /// </summary>
    private void RebuildBusRows()
    {
        DetachBusRows();
        InputRows.Clear();
        OutputRows.Clear();
        foreach (var row in SignalBusGrouping.GroupInputs(Inputs))
            InputRows.Add(row);
        foreach (var row in SignalBusGrouping.GroupOutputs(Outputs))
            OutputRows.Add(row);
    }

    /// <summary>Unsubscribes every bus row from its members before the rows go away.</summary>
    private void DetachBusRows()
    {
        foreach (var bus in InputRows.OfType<LogicSignalBusInputViewModel>())
            bus.Detach();
        foreach (var bus in OutputRows.OfType<LogicSignalBusOutputViewModel>())
            bus.Detach();
    }
}

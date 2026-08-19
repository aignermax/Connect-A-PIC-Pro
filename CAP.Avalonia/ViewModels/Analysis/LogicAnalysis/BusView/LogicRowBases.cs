using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

/// <summary>
/// One row of the Logic panel's Inputs list (issue #1068): either a single
/// <see cref="LogicNetworkInputViewModel"/> toggle or a
/// <see cref="LogicSignalBusInputViewModel"/> bus header grouping an indexed signal family.
/// </summary>
public abstract class LogicInputRowViewModel : ObservableObject
{
}

/// <summary>
/// One row of the Logic panel's Outputs list (issue #1068): either a single
/// <see cref="LogicNetworkOutputViewModel"/> indicator or a
/// <see cref="LogicSignalBusOutputViewModel"/> bus header grouping an indexed signal family.
/// </summary>
public abstract class LogicOutputRowViewModel : ObservableObject
{
}

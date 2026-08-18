using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Canvas overlay holding the live logic state (0/1) of every gate group while the
/// Logic panel's network is built (issue #994). The <see cref="DesignCanvasViewModel"/>
/// owns the single instance as the canvas-side source of truth; the Logic panel writes
/// the freshly evaluated gate output bits into it after every evaluation and clears it
/// when the network is discarded (rebuild, cancel, failure, design edit, load), so the
/// badges always mirror exactly the data the panel's output list shows.
/// </summary>
public sealed class LogicGateStateOverlay
{
    /// <summary>The current badge per gate output pin, empty while no network is shown.</summary>
    public ObservableCollection<LogicGateBadgeViewModel> Badges { get; } = new();

    /// <summary>Raised after every badge mutation (rebuild or clear) so the canvas repaints.</summary>
    public event EventHandler? StatesChanged;

    /// <summary>Replaces all badges with one freshly evaluated state per gate output pin.</summary>
    /// <param name="states">One entry per gate output pin of the evaluated network.</param>
    public void ShowStates(IEnumerable<LogicGateBadgeState> states)
    {
        Badges.Clear();
        foreach (var state in states)
        {
            Badges.Add(new LogicGateBadgeViewModel(state.GroupName, state.PinName, state.IsOne));
        }
        StatesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes every badge — the network behind them is gone.</summary>
    public void Clear()
    {
        if (Badges.Count == 0)
            return;
        Badges.Clear();
        StatesChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One evaluated gate output bit destined for a canvas badge.</summary>
/// <param name="GroupName">Name of the gate group on the canvas.</param>
/// <param name="PinName">The gate's output pin the bit belongs to.</param>
/// <param name="IsOne">The evaluated bit.</param>
public readonly record struct LogicGateBadgeState(string GroupName, string PinName, bool IsOne);

/// <summary>
/// One logic-state badge on a gate group: the evaluated bit of one of the gate's output
/// pins. Single-output gates — every gate of the shipped logic examples — get exactly one
/// badge; a multi-output gate gets one badge per output pin, stacked on the group.
/// </summary>
public sealed class LogicGateBadgeViewModel
{
    /// <summary>Initializes the badge with its first evaluated bit.</summary>
    public LogicGateBadgeViewModel(string groupName, string pinName, bool isOne)
    {
        GroupName = groupName;
        PinName = pinName;
        IsOne = isOne;
    }

    /// <summary>Name of the gate group the badge sits on — the network's gate id.</summary>
    public string GroupName { get; }

    /// <summary>The gate output pin the bit was read from.</summary>
    public string PinName { get; }

    /// <summary>The currently evaluated bit at this pin.</summary>
    public bool IsOne { get; }

    /// <summary>The bit as display text ("1" or "0").</summary>
    public string BitText => IsOne ? "1" : "0";
}

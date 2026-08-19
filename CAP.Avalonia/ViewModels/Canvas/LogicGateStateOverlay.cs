using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Canvas overlay holding the live logic state (0/1) of every gate group while the
/// Logic panel's network is built (issue #994). The <see cref="DesignCanvasViewModel"/>
/// owns the single instance as the canvas-side source of truth; the Logic panel writes
/// the freshly evaluated gate output bits into it after every evaluation and clears it
/// when the network is discarded (rebuild, cancel, failure, design edit, load), so the
/// badges always mirror exactly the data the panel's output list shows. A gate pin
/// carrying a persisted signal name shows that name on its badge: input pins get an
/// additional named chip (<c>A0 = 1</c>, issue #1051), and an output pin whose network
/// tap is signal-named carries the name on its own chip (<c>S0 = 1</c>, issue #1067),
/// so a student can tell which badge reads which signal.
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
            Badges.Add(new LogicGateBadgeViewModel(state.GroupName, state.PinName, state.IsOne, state.SignalName));
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
/// <param name="PinName">The gate pin the bit belongs to.</param>
/// <param name="IsOne">The evaluated bit.</param>
/// <param name="SignalName">
/// The persisted signal name of the badge's pin: a named gate input pin (issue #1051)
/// or an output pin whose network tap is signal-named (issue #1067); null for
/// anonymous badges.
/// </param>
public readonly record struct LogicGateBadgeState(string GroupName, string PinName, bool IsOne, string? SignalName = null);

/// <summary>
/// One logic-state badge on a gate group: the evaluated bit of one of the gate's output
/// pins. Single-output gates — every gate of the shipped logic examples — get exactly one
/// badge; a multi-output gate gets one badge per output pin, stacked on the group.
/// A pin carrying a persisted signal name shows the name next to the live bit: input
/// pins get an additional named chip (<c>A0 = 1</c>, issue #1051), and an output pin
/// whose network tap is signal-named carries the name on its own chip
/// (<c>S0 = 1</c>, issue #1067); unnamed pins keep the plain 0/1 chip exactly.
/// </summary>
public sealed class LogicGateBadgeViewModel
{
    /// <summary>Initializes the badge with its first evaluated bit.</summary>
    public LogicGateBadgeViewModel(string groupName, string pinName, bool isOne, string? signalName = null)
    {
        GroupName = groupName;
        PinName = pinName;
        IsOne = isOne;
        SignalName = signalName;
    }

    /// <summary>Name of the gate group the badge sits on — the network's gate id.</summary>
    public string GroupName { get; }

    /// <summary>The gate pin the bit was read from.</summary>
    public string PinName { get; }

    /// <summary>The currently evaluated bit at this pin.</summary>
    public bool IsOne { get; }

    /// <summary>The persisted signal name of a named input or output pin, or null for anonymous badges.</summary>
    public string? SignalName { get; }

    /// <summary>True when the badge carries a persisted signal name.</summary>
    public bool HasSignalName => SignalName != null;

    /// <summary>The bit as display text ("1" or "0").</summary>
    public string BitText => IsOne ? "1" : "0";

    /// <summary>
    /// The badge's full display text: <c>A0 = 1</c> for a named signal, the plain
    /// bit ("1" or "0") for an anonymous badge.
    /// </summary>
    public string LabelText => HasSignalName ? $"{SignalName} = {BitText}" : BitText;
}

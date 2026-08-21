using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Run (auto-clock) half of <see cref="LogicPanelViewModel"/> (issue #1111, rung 5
/// "watch it execute"): the Run button turns the Step button into a self-clocking
/// circuit. Every tick of the injected <see cref="ILogicRunClock"/> executes exactly
/// one Step press (re-settle with the visible inputs → Step → refresh badges,
/// register readout and bus rows), so the circuit visibly runs at the selected
/// cadence. Input toggles stay interactive while running — a flipped input simply
/// lands in the next tick, which is the honest behavior. Run stops when the network
/// is rebuilt/cleared or a design edit invalidates it (both go through
/// <see cref="ClearNetwork"/>), and the view calls <see cref="StopRun"/> when the
/// panel leaves the visual tree. The ViewModel stays timer-free: production injects
/// a <see cref="DispatcherLogicRunClock"/>, tests inject a fake and fire ticks
/// synchronously.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>The selectable clock rates: 0.5 s, 1 s and 2 s per clock.</summary>
    private static readonly TimeSpan[] RunIntervalValues =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    };

    /// <summary>Index of the preselected clock rate in <see cref="RunIntervalValues"/> (1 s per clock).</summary>
    private const int DefaultRunIntervalIndex = 1;

    private readonly ILogicRunClock _runClock;

    /// <summary>Initializes the panel; <paramref name="runClock"/> defaults to the dispatcher-based production clock.</summary>
    public LogicPanelViewModel(ILogicRunClock? runClock = null)
    {
        _runClock = runClock ?? new DispatcherLogicRunClock();
        _runClock.Tick += OnRunClockTick;
        RunIntervalOptions = RunIntervalValues.Select(value => new LogicRunIntervalOption(value)).ToList();
        _selectedRunInterval = RunIntervalOptions[DefaultRunIntervalIndex];
    }

    /// <summary>True while the network clocks itself (the button shows Stop).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStopText))]
    private bool _isRunning;

    /// <summary>The selectable clock rates shown in the interval selector.</summary>
    public IReadOnlyList<LogicRunIntervalOption> RunIntervalOptions { get; }

    /// <summary>The selected tick interval; changing it mid-run re-arms the clock at the new cadence.</summary>
    [ObservableProperty]
    private LogicRunIntervalOption _selectedRunInterval;

    /// <summary>Label of the Run/Stop button — Run at rest, Stop while the auto-clock runs.</summary>
    public string RunStopText =>
        Translate(IsRunning ? "LogicPanel.RunStop" : "LogicPanel.Run");

    /// <summary>
    /// Run starts the auto-clock at the selected interval; Stop halts it. Only
    /// meaningful with at least one register — a purely combinational network has
    /// nothing to clock (same gating as Step).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRegisters))]
    private void ToggleRun()
    {
        if (IsRunning)
        {
            StopRun();
            return;
        }
        if (_network == null || !HasRegisters || SelectedRunInterval == null)
            return;
        IsRunning = true;
        _runClock.Start(SelectedRunInterval.Interval);
    }

    /// <summary>
    /// Stops the auto-clock; the network keeps its committed state. Called by the
    /// Stop button, by every network clear (rebuild, cancel, design edit), and by
    /// the view when the panel leaves the visual tree.
    /// </summary>
    public void StopRun()
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        _runClock.Stop();
    }

    /// <summary>One auto-clock tick ≡ one Step press; a stray tick queued before Stop is ignored.</summary>
    private void OnRunClockTick(object? sender, EventArgs e)
    {
        if (!IsRunning)
            return;
        ClockOnce();
    }

    /// <summary>Re-arms the running clock when the user picks another cadence mid-run.</summary>
    partial void OnSelectedRunIntervalChanged(LogicRunIntervalOption value)
    {
        if (IsRunning && value != null)
            _runClock.Start(value.Interval);
    }
}

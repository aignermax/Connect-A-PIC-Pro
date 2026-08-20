namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Tick source behind the Logic panel's Run (auto-clock) mode (issue #1111). The
/// ViewModel stays timer-free so tests stay deterministic: production wires a
/// <see cref="DispatcherLogicRunClock"/>, tests inject a fake that records the
/// requested interval and fires ticks synchronously (mirrors the timer-free
/// auto-play seam of issue #1069).
/// </summary>
public interface ILogicRunClock
{
    /// <summary>Raised at the requested interval between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    event EventHandler? Tick;

    /// <summary>(Re)starts the ticking at <paramref name="interval"/>; a running clock re-arms at the new cadence.</summary>
    void Start(TimeSpan interval);

    /// <summary>Stops the ticking; no further ticks until the next <see cref="Start"/>.</summary>
    void Stop();
}

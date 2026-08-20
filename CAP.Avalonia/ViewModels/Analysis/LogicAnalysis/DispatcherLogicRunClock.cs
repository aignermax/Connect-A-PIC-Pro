using Avalonia.Threading;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Production <see cref="ILogicRunClock"/> backed by a <see cref="DispatcherTimer"/>:
/// the ticks arrive on the UI thread, so a tick can refresh the panel's observable
/// collections and the canvas badges directly.
/// </summary>
public sealed class DispatcherLogicRunClock : ILogicRunClock
{
    private readonly DispatcherTimer _timer = new();

    /// <summary>Initializes the clock; the timer forwards every elapsed interval as <see cref="Tick"/>.</summary>
    public DispatcherLogicRunClock() => _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    public event EventHandler? Tick;

    /// <inheritdoc/>
    public void Start(TimeSpan interval)
    {
        _timer.Interval = interval;
        _timer.Start();
    }

    /// <inheritdoc/>
    public void Stop() => _timer.Stop();
}

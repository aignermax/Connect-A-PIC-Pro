using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command for toggling a coupler's laser between input (on) and listen-only
/// output (off), issue #690. Provides undo/redo support like the neighbouring
/// group-lock icon toggle (<see cref="ToggleGroupLockCommand"/>).
/// </summary>
public class ToggleLaserCommand : IUndoableCommand
{
    private readonly ComponentViewModel _coupler;
    private readonly bool _wasEnabled;

    /// <summary>
    /// Creates a command that toggles the given coupler's laser state.
    /// </summary>
    /// <param name="coupler">The light-source component whose laser is toggled.</param>
    public ToggleLaserCommand(ComponentViewModel coupler)
    {
        _coupler = coupler ?? throw new ArgumentNullException(nameof(coupler));
        _wasEnabled = coupler.LaserConfig?.IsEnabled ?? true;
    }

    /// <summary>Human-readable description for the undo stack.</summary>
    public string Description =>
        _wasEnabled
            ? $"Switch laser off — '{_coupler.Name}' becomes an output"
            : $"Switch laser on — '{_coupler.Name}' becomes an input";

    /// <summary>Applies the toggle.</summary>
    public void Execute()
    {
        if (_coupler.LaserConfig != null)
            _coupler.LaserConfig.IsEnabled = !_wasEnabled;
    }

    /// <summary>Restores the previous laser state.</summary>
    public void Undo()
    {
        if (_coupler.LaserConfig != null)
            _coupler.LaserConfig.IsEnabled = _wasEnabled;
    }
}

using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Composite undoable command for the analysis-output eyedropper (#754): designates the
/// clicked coupler AND — when its laser is still emitting — switches the laser off, as a
/// single history entry. One Ctrl+Z reverts both, restoring the previous designation and
/// the laser state; the pre-round-4 split (undoable laser toggle + plain designation
/// mutation) left an orphaned designation behind (#762 review, finding [2]).
/// </summary>
public class PickAnalysisOutputCommand : IUndoableCommand
{
    private readonly ComponentViewModel _coupler;
    private readonly AnalysisOutputDesignation _designation;
    private readonly bool _laserWasOn;
    private readonly Guid? _previousCouplerId;

    /// <summary>Creates the command; captures the laser state and previous designation.</summary>
    /// <param name="coupler">The light-source component being designated as THE output.</param>
    /// <param name="designation">The canvas-wide analysis-output designation.</param>
    public PickAnalysisOutputCommand(ComponentViewModel coupler, AnalysisOutputDesignation designation)
    {
        _coupler = coupler ?? throw new ArgumentNullException(nameof(coupler));
        _designation = designation ?? throw new ArgumentNullException(nameof(designation));
        _laserWasOn = coupler.LaserConfig?.IsEnabled ?? false;
        _previousCouplerId = designation.CouplerId;
    }

    /// <summary>Human-readable description for the undo stack.</summary>
    public string Description => $"Pick '{_coupler.Name}' as analysis output";

    /// <summary>Designates the coupler and switches a still-emitting laser off.</summary>
    public void Execute()
    {
        if (_laserWasOn && _coupler.LaserConfig != null)
            _coupler.LaserConfig.IsEnabled = false;
        _designation.Designate(_coupler.Component.Id);
    }

    /// <summary>Restores the previous designation and the laser state.</summary>
    public void Undo()
    {
        if (_laserWasOn && _coupler.LaserConfig != null)
            _coupler.LaserConfig.IsEnabled = true;
        if (_previousCouplerId is Guid previous)
            _designation.Designate(previous);
        else
            _designation.Clear();
    }
}

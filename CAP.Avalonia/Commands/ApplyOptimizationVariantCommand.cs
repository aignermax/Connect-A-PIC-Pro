using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Undoable command that applies an optimizer variant's slider values to the
/// canvas components. Undo restores every slider to its previous value, so a
/// user can freely compare variants against the original design.
/// </summary>
public class ApplyOptimizationVariantCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(ComponentViewModel Component, double NewValue)> _assignments;
    private readonly double[] _previousValues;
    private readonly string _variantName;

    /// <summary>Creates the command, capturing the current slider values for undo.</summary>
    /// <param name="assignments">Target components with the slider values to apply.</param>
    /// <param name="variantName">Short label for the undo stack (e.g. "variant #2").</param>
    public ApplyOptimizationVariantCommand(
        IReadOnlyList<(ComponentViewModel Component, double NewValue)> assignments,
        string variantName)
    {
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        _variantName = variantName ?? throw new ArgumentNullException(nameof(variantName));
        _previousValues = assignments.Select(a => a.Component.SliderValue).ToArray();
    }

    /// <summary>Human-readable description for the undo stack.</summary>
    public string Description => $"Apply optimization {_variantName}";

    /// <summary>Applies the variant's slider values.</summary>
    public void Execute()
    {
        foreach (var (component, newValue) in _assignments)
            component.SliderValue = newValue;
    }

    /// <summary>Restores the slider values captured before the variant was applied.</summary>
    public void Undo()
    {
        for (int i = 0; i < _assignments.Count; i++)
            _assignments[i].Component.SliderValue = _previousValues[i];
    }
}

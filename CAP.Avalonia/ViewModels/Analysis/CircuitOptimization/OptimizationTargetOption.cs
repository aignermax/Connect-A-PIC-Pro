namespace CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;

/// <summary>
/// One selectable optimization target in the panel: either the summed power at
/// all output couplers ("total transmission") or the power arriving at a single
/// coupler port.
/// </summary>
public class OptimizationTargetOption
{
    /// <summary>Name shown in the target ComboBox.</summary>
    public string DisplayName { get; }

    /// <summary>Pin GUIDs whose incoming power forms the metric.</summary>
    public IReadOnlyList<Guid> PinIds { get; }

    /// <summary>Creates a target option.</summary>
    public OptimizationTargetOption(string displayName, IReadOnlyList<Guid> pinIds)
    {
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        PinIds = pinIds ?? throw new ArgumentNullException(nameof(pinIds));
    }

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

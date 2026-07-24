namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// One entry of a registry browser filter dropdown (process or trust status):
/// the raw filter value — null means "no filtering" (the "All …" entry) —
/// plus the label shown in the ComboBox.
/// </summary>
/// <param name="Value">Raw value to filter on; null disables this filter.</param>
/// <param name="Label">Human-readable dropdown label.</param>
public sealed record RegistryFilterOption(string? Value, string Label)
{
    /// <summary>ComboBoxes render options via ToString.</summary>
    public override string ToString() => Label;
}

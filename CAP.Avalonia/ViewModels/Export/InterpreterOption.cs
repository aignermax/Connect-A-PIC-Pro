namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// One selectable interpreter in the unified list on the GDS-Export settings page —
/// either a managed environment or a discovered system Python. The interpreter path
/// is shown as a label so entries with equal names stay distinguishable.
/// </summary>
/// <param name="DisplayText">Primary line, e.g. "Managed · nazca · Python 3.11 · Nazca 0.6.1".</param>
/// <param name="Path">Full path to the interpreter executable (secondary label).</param>
/// <param name="IsActive">True when this interpreter is the currently configured one.</param>
/// <param name="ManagedName">Registry name when this is a managed environment; null for system Pythons.</param>
public sealed record InterpreterOption(string DisplayText, string Path, bool IsActive, string? ManagedName);

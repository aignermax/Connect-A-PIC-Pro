namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// A managed Python environment offered as an interpreter candidate on the
/// GDS-Export settings page. Deliberately a plain DTO: the GDS-export slice
/// never imports the environment-manager slice — the DI layer maps registry
/// entries into this type. Packages beyond Nazca (e.g. gdsfactory, #581)
/// extend <see cref="DisplayText"/> later without UI changes.
/// </summary>
/// <param name="Name">Registry name of the environment (activation key).</param>
/// <param name="PythonExecutable">Full path to the environment's interpreter.</param>
/// <param name="DisplayText">Human-readable list entry, e.g. "Managed · nazca · Python 3.11 · Nazca 0.6.1".</param>
public sealed record ManagedEnvCandidate(string Name, string PythonExecutable, string DisplayText);

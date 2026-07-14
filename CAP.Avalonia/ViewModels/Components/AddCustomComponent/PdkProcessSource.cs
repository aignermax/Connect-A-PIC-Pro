namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// Where <see cref="CreateCustomPdkViewModel"/> gets the fabrication process for a newly
/// created named user PDK: adopted from an already-loaded process, or authored from scratch.
/// </summary>
public enum PdkProcessSource
{
    /// <summary>Adopt an already-loaded process (see <see cref="CreateCustomPdkViewModel.SelectedExistingProcess"/>).</summary>
    UseExisting,

    /// <summary>Define a brand-new process via <see cref="CreateCustomPdkViewModel.ProcessDefinitionEditor"/>.</summary>
    DefineNew,
}

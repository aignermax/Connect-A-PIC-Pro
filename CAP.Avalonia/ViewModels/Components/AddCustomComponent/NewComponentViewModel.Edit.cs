using System.Linq;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// Edit-mode entry point for <see cref="NewComponentViewModel"/>: prefills the wizard from an
/// existing custom component's <see cref="ComponentTemplate"/> so the user can revise it in
/// place, rather than authoring a new component from scratch. Split out purely to keep each
/// file under the project's line-count limit; still one partial class, one responsibility.
/// </summary>
public partial class NewComponentViewModel
{
    /// <summary>
    /// The name of the component being edited, captured by <see cref="LoadForEdit"/>. Used by
    /// <c>Save</c> to distinguish a self-overwrite (re-saving the same component, which must
    /// proceed without a collision prompt) from a rename onto a <em>different</em> existing
    /// component (which must still go through the normal <c>ConfirmOverwrite</c> path so it is
    /// never silently clobbered). Null outside edit mode.
    /// </summary>
    private string? _editingOriginalName;

    /// <summary>
    /// Prefills <see cref="NewComponentViewModel.ComponentName"/>, <see cref="NewComponentViewModel.Code"/>,
    /// <see cref="NewComponentViewModel.SelectedBackend"/>, and the fixed target PDK from
    /// <paramref name="template"/>, then sets <see cref="NewComponentViewModel.IsEditMode"/>.
    /// The target PDK is resolved by matching <see cref="ComponentTemplate.PdkSource"/> against
    /// the existing custom PDKs in <see cref="NewComponentViewModel.PdkChoices"/> — never the
    /// "New PDK…" sentinel, so this never invokes <see cref="NewComponentViewModel.CreateNewPdk"/>.
    /// Also records the original name in <see cref="_editingOriginalName"/> so <c>Save</c> can
    /// tell a self-overwrite (no prompt) from a rename onto another existing component (still
    /// prompts). Re-saving the edited component under its own name overwrites-by-name via
    /// <c>AppendToExistingPdk</c>, which is exactly the desired edit behaviour. Missing stored
    /// code, or no matching custom PDK, is reported via <see cref="NewComponentViewModel.StatusText"/>
    /// rather than guessed; a missing PDK also leaves <see cref="NewComponentViewModel.IsEditMode"/>
    /// false (defensive — there is no valid overwrite target).
    /// </summary>
    public void LoadForEdit(ComponentTemplate template)
    {
        var match = PdkChoices.FirstOrDefault(c => !c.IsNewPdk && c.Pdk?.Name == template.PdkSource);
        if (match is null)
        {
            StatusText = $"Cannot edit '{template.Name}': its PDK '{template.PdkSource}' is not a custom PDK.";
            return;
        }

        ComponentName = template.Name;
        SelectedBackend = template.RawCodeBackend == "nazca" ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        // Assigned last: OnSelectedBackendChanged may auto-load a starter snippet when Code is
        // still blank, and that auto-load must never win over the template's own stored code.
        Code = template.RawCode ?? string.Empty;
        if (string.IsNullOrEmpty(template.RawCode))
        {
            StatusText = "No stored code for this component — enter code to edit.";
        }
        SelectedPdkChoice = match;

        _editingOriginalName = template.Name;
        IsEditMode = true;
    }
}

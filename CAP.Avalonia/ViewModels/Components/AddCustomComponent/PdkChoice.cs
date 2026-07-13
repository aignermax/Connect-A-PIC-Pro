using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// One selectable entry in <see cref="NewComponentViewModel.PdkChoices"/>: either an existing
/// named custom PDK (<see cref="Pdk"/> set, <see cref="IsNewPdk"/> false) or the trailing
/// "New PDK…" sentinel (<see cref="Pdk"/> null, <see cref="IsNewPdk"/> true). Selecting the
/// sentinel invokes <see cref="NewComponentViewModel.CreateNewPdk"/> rather than being a savable
/// target itself — chosen over a raw <c>bool</c> flag alongside <c>AvailableCustomPdks</c>
/// because a single strongly-typed dropdown item (rather than a list plus a side flag) binds
/// cleanly to one Avalonia <c>ComboBox</c> with one <c>SelectedItem</c>.
/// </summary>
public sealed record PdkChoice(string DisplayName, UserPdkInfo? Pdk, bool IsNewPdk)
{
    /// <summary>The sentinel entry always appended as the dropdown's last item.</summary>
    public static readonly PdkChoice NewPdkSentinel = new("New PDK…", null, true);

    /// <summary>Wraps an existing named custom PDK as a selectable dropdown entry.</summary>
    public static PdkChoice For(UserPdkInfo pdk) => new(pdk.Name, pdk, false);
}

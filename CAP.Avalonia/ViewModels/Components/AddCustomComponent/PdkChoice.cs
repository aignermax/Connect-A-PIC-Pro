using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public sealed record PdkChoice(string DisplayName, UserPdkInfo? Pdk, bool IsNewPdk)
{
    public static readonly PdkChoice NewPdkSentinel = new("New PDK…", null, true);

    public static PdkChoice For(UserPdkInfo pdk) => new(pdk.Name, pdk, false);
}

using CAP.Avalonia.ViewModels.Panels.PdkTrash;
using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Wires the PDK-Management trash flyout (recover deleted PDKs / removed components) into the
/// left panel. The mirror image of <see cref="LeftPanelViewModel.UnregisterPdk"/> /
/// <see cref="RemoveCustomComponent"/>: where those delete-to-<c>.trash</c> and DEregister from
/// the library, this restores from <c>.trash</c> and RE-registers via the same
/// <see cref="RegisterCreatedPdk"/> / <see cref="RegisterSavedCustomComponent"/> hooks.
/// Split into its own partial to keep <c>LeftPanelViewModel.cs</c> under the line-count limit.
/// </summary>
public partial class LeftPanelViewModel
{
    private PdkTrashViewModel? _pdkTrash;

    /// <summary>
    /// The PDK trash ViewModel, created lazily over the SAME user-PDK root the delete path uses
    /// (via <see cref="UserPdkStore.CreateTrashService"/>), or the default root when no store is
    /// wired (test harnesses). Bound by the small trash button in the PDK-Management header.
    /// </summary>
    public PdkTrashViewModel PdkTrash => _pdkTrash ??= CreatePdkTrash();

    private PdkTrashViewModel CreatePdkTrash()
    {
        var trashService = _addCustomComponentDeps?.UserPdkStore?.CreateTrashService()
                           ?? PdkTrashService.CreateDefault();
        var vm = new PdkTrashViewModel(trashService, _errorConsole)
        {
            OnRestored = ApplyRestore,
        };
        return vm;
    }

    /// <summary>
    /// Re-registers a restored PDK (whole file) or restored components into the running library —
    /// exactly what <see cref="UnregisterPdk"/> / <see cref="RemoveCustomComponent"/> undid.
    /// </summary>
    private void ApplyRestore(PdkTrashRestoreResult result)
    {
        if (result.Kind == PdkTrashKind.DeletedPdk)
        {
            RegisterCreatedPdk(result.RestoredPdkPath);
            return;
        }

        foreach (var component in result.RestoredComponents)
            RegisterSavedCustomComponent(component, result.PdkName, result.RestoredPdkPath);
    }
}

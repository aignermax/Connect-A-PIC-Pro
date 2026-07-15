using CAP.Avalonia.ViewModels.Panels.PdkTrash;
using CAP_DataAccess.Components.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    private PdkTrashViewModel? _pdkTrash;

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

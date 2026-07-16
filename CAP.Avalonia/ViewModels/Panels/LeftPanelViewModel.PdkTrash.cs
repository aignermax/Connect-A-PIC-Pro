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

        // A restored trash entry is a real file in user-pdks: like the startup path, the file
        // is the truth about a fork's existence, so a restore under a bundled PDK's name may
        // shadow the built-in entry again (it restores the user's fork).
        foreach (var component in result.RestoredComponents)
            RegisterSavedCustomComponent(component, result.PdkName, result.RestoredPdkPath, savedViaBundledFork: true);
    }
}

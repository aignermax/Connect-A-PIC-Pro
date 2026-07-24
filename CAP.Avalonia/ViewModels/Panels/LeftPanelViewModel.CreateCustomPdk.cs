namespace CAP.Avalonia.ViewModels.Panels;

public partial class LeftPanelViewModel
{
    internal void RegisterCreatedPdk(string filePath)
    {
        if (PdkManager.IsPdkLoaded(filePath))
            return;

        TryReloadUserPdk(filePath);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }
}

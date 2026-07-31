using Avalonia.Controls;
using CAP.Avalonia.ViewModels.GdsImport;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>
/// Code-behind for the GDS import dialog (issue #808). The .gds file was chosen
/// before the dialog opens, so the analysis starts automatically on open —
/// same pattern as <see cref="PdkImport.PdkImportWizardWindow"/>.
/// </summary>
public partial class GdsImportDialog : Window
{
    /// <summary>Initializes a new <see cref="GdsImportDialog"/>.</summary>
    public GdsImportDialog()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not GdsImportDialogViewModel vm)
            return;

        vm.OnClose = Close;
        _ = vm.StartAnalysisAsync();
    }
}

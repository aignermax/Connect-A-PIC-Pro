using Avalonia.Controls;
using Avalonia.Input;
using CAP.Avalonia.ViewModels.GdsImport;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>
/// Code-behind for the GDS import dialog. The .gds file was chosen
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

    /// <summary>Marks the port-label field as the census click target.</summary>
    private void OnPortLayersGotFocus(object? sender, GotFocusEventArgs e) =>
        SetActiveLayerField(GdsLayerFieldTarget.PortLabels);

    /// <summary>Marks the waveguide field as the census click target.</summary>
    private void OnWaveguideLayersGotFocus(object? sender, GotFocusEventArgs e) =>
        SetActiveLayerField(GdsLayerFieldTarget.Waveguide);

    /// <summary>Marks the metal field as the census click target.</summary>
    private void OnMetalLayersGotFocus(object? sender, GotFocusEventArgs e) =>
        SetActiveLayerField(GdsLayerFieldTarget.Metal);

    private void SetActiveLayerField(GdsLayerFieldTarget target)
    {
        if (DataContext is GdsImportDialogViewModel vm)
            vm.ActiveLayerField = target;
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        // A close mid-import must not leave the background run mutating a canvas
        // the user no longer sees: cancel and release the per-run cancellation
        // source. (Window-lifecycle wiring is not coverable headless — the VM
        // half, GdsImportDialogViewModel.OnWindowClosed, is unit-tested.)
        if (DataContext is GdsImportDialogViewModel vm)
            vm.OnWindowClosed();
        base.OnClosed(e);
    }
}

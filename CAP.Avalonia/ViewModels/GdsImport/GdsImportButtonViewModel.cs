using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// ViewModel behind the GDS import entry points: the library panel's
/// "Import GDS" button, the toolbar's Import button, and the File→Open dialog's
/// .gds/.gdsii route (<see cref="OpenGdsImportDialogForFileAsync"/>). Kept separate
/// from <see cref="LeftPanelViewModel"/> on purpose: the left panel already carries
/// component-library, PDK-management and trash responsibilities, and GDS import is
/// a self-contained flow (file pick → dialog → canvas placement) that only needs
/// the panel's template list and registration callback.
/// </summary>
public partial class GdsImportButtonViewModel : ObservableObject
{
    private readonly GdsImportService _importService;
    private readonly GdsPlacementExecutor _placementExecutor;
    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Open-file dialog service, injected from the view layer (MainWindow).</summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Shows the import dialog for a fully built <see cref="GdsImportDialogViewModel"/>.
    /// Wired by <c>MainWindow</c> code-behind (same pattern as
    /// <c>LeftPanelViewModel.ShowNewComponentWindowAsync</c>).
    /// </summary>
    public Func<GdsImportDialogViewModel, Task>? ShowImportDialogAsync { get; set; }

    /// <summary>Raised with a user-presentable status line (wired to the main status bar).</summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>
    /// Callback to zoom the canvas so the whole content fits the viewport after a
    /// successful import placement — the same semantics as
    /// <c>FileOperationsViewModel.ZoomToFitAfterLoad</c>: invoked with a fallback
    /// viewport size the view layer replaces with the real one. Wired by
    /// <see cref="MainViewModel"/> and handed through to the dialog ViewModel,
    /// which owns the import's completion point.
    /// </summary>
    public Action<double, double>? ZoomToFitAfterImport { get; set; }

    /// <summary>
    /// Callback to sync the chip-size settings ViewModel when an import
    /// auto-enlarged the chip to fit the design (same wiring pattern as
    /// <c>FileOperationsViewModel.ApplyChipSizeAfterLoad</c>). Wired by
    /// <see cref="MainViewModel"/> and handed through to the dialog ViewModel.
    /// </summary>
    public Action<double, double>? ApplyChipSizeAfterImport { get; set; }

    /// <summary>Initializes a new <see cref="GdsImportButtonViewModel"/>.</summary>
    /// <param name="importService">Import orchestration: analysis, draft mapping, PDK persistence.</param>
    /// <param name="placementExecutor">Places the imported circuit onto the canvas.</param>
    /// <param name="errorConsole">
    /// Optional error console handed through to the dialog ViewModel, so import
    /// warnings/failures survive as copyable entries after the dialog closes.
    /// </param>
    public GdsImportButtonViewModel(
        GdsImportService importService,
        GdsPlacementExecutor placementExecutor,
        ErrorConsoleService? errorConsole = null)
    {
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _placementExecutor = placementExecutor ?? throw new ArgumentNullException(nameof(placementExecutor));
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Picks a .gds file and opens the import dialog. The file is chosen BEFORE
    /// the dialog opens so the dialog can start its analysis immediately.
    /// </summary>
    [RelayCommand]
    private async Task OpenGdsImportDialog()
    {
        if (FileDialogService is null)
        {
            UpdateStatus?.Invoke(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
            return;
        }

        string? gdsPath;
        try
        {
            gdsPath = await FileDialogService.ShowOpenFileDialogAsync(
                LocalizationService.Instance.Translate("GdsImport.OpenFileTitle"),
                "GDS files (*.gds;*.gdsii)|*.gds;*.gdsii|All files (*.*)|*.*");
        }
        catch (Exception ex)
        {
            // A failing file dialog must not escape the command as an unhandled
            // task exception — surface it on the status bar instead (same pattern
            // as the unavailable case above).
            UpdateStatus?.Invoke(string.Format(
                LocalizationService.Instance.Translate("GdsImport.StatusOpenFailed"), ex.Message));
            return;
        }

        if (string.IsNullOrEmpty(gdsPath))
            return;

        await OpenGdsImportDialogForFileAsync(gdsPath);
    }

    /// <summary>
    /// Opens the import dialog for an already-picked .gds file — e.g. a GDS picked
    /// in the File→Open design dialog, which <c>FileOperationsViewModel</c> routes
    /// here instead of the .lun load path. The dialog analyzes the file when it
    /// opens, so the path only needs to exist.
    /// </summary>
    /// <param name="gdsPath">Absolute path of the .gds/.gdsii file to import.</param>
    public async Task OpenGdsImportDialogForFileAsync(string gdsPath)
    {
        if (ShowImportDialogAsync is null)
        {
            UpdateStatus?.Invoke(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
            return;
        }

        try
        {
            var dialogViewModel = new GdsImportDialogViewModel(gdsPath, _importService, _placementExecutor, _errorConsole);
            // The dialog owns the import's completion point, so it fires the zoom
            // (set by MainViewModel; null in headless runs) once its import task
            // completes with at least one placed component.
            dialogViewModel.ZoomToFitAfterImport = ZoomToFitAfterImport;
            dialogViewModel.ApplyChipSizeAfterImport = ApplyChipSizeAfterImport;
            await ShowImportDialogAsync(dialogViewModel);
        }
        catch (Exception ex)
        {
            // A failing dialog host must not escape as an unhandled task exception
            // — surface it on the status bar instead.
            UpdateStatus?.Invoke(string.Format(
                LocalizationService.Instance.Translate("GdsImport.StatusOpenFailed"), ex.Message));
        }
    }
}

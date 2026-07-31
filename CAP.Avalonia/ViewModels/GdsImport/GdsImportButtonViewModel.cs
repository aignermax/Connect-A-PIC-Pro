using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_DataAccess.Components.AddCustomComponent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// ViewModel behind the library panel's "Import GDS" button (issue #808). Kept
/// separate from <see cref="LeftPanelViewModel"/> on purpose: the left panel
/// already carries component-library, PDK-management and trash responsibilities,
/// and GDS import is a self-contained flow (file pick → dialog → canvas placement)
/// that only needs the panel's template list and registration callback.
/// </summary>
public partial class GdsImportButtonViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly Commands.CommandManager _commandManager;
    private readonly LeftPanelViewModel _leftPanel;

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

    /// <summary>Initializes a new <see cref="GdsImportButtonViewModel"/>.</summary>
    /// <param name="canvas">Canvas the imported circuit is placed onto.</param>
    /// <param name="commandManager">Undo stack for the placement commands.</param>
    /// <param name="leftPanel">
    /// Component library: supplies the loaded templates for known-cell resolution
    /// and registers the imported components at runtime.
    /// </param>
    public GdsImportButtonViewModel(
        DesignCanvasViewModel canvas,
        Commands.CommandManager commandManager,
        LeftPanelViewModel leftPanel)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _leftPanel = leftPanel ?? throw new ArgumentNullException(nameof(leftPanel));
    }

    /// <summary>
    /// Picks a .gds file, builds the import service + placement executor, and opens
    /// the import dialog. The file is chosen BEFORE the dialog opens so the dialog
    /// can start its analysis immediately.
    /// </summary>
    [RelayCommand]
    private async Task OpenGdsImportDialog()
    {
        if (FileDialogService is null || ShowImportDialogAsync is null)
        {
            UpdateStatus?.Invoke(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
            return;
        }

        var gdsPath = await FileDialogService.ShowOpenFileDialogAsync(
            LocalizationService.Instance.Translate("GdsImport.OpenFileTitle"),
            "GDS files (*.gds;*.gdsii)|*.gds;*.gdsii|All files (*.*)|*.*");
        if (string.IsNullOrEmpty(gdsPath))
            return;

        var importService = new GdsImportService(
            UserPdkStore.CreateDefault(),
            () => _leftPanel.AllTemplates.ToList(),
            // Lambda, not the method group: the optional savedViaBundledFork
            // parameter keeps it from matching the 3-argument Action directly.
            (draft, pdkName, filePath) => _leftPanel.RegisterSavedCustomComponent(draft, pdkName, filePath));
        var placementExecutor = new GdsPlacementExecutor(
            _canvas, _commandManager, () => _leftPanel.AllTemplates.ToList());

        await ShowImportDialogAsync(new GdsImportDialogViewModel(gdsPath, importService, placementExecutor));
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;

/// <summary>
/// ViewModel for the shared analysis-output header in the analysis dock (issue #754):
/// shows which coupler is designated as THE output for the Eye/BER and Transient tabs,
/// starts the eyedropper picker, and clears the designation. Reads and writes the
/// design-wide <see cref="AnalysisOutputDesignation"/> on the canvas.
/// </summary>
public partial class AnalysisOutputPanelViewModel : ObservableObject
{
    private DesignCanvasViewModel? _canvas;

    /// <summary>Display name of the designated output coupler, or a localized "(automatic)".</summary>
    [ObservableProperty]
    private string _outputDisplayName = "";

    /// <summary>True while a coupler is designated (enables the Clear button).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private bool _hasOutput;

    /// <summary>
    /// Callback that activates the canvas picker mode. Wired by <c>MainViewModel</c> to
    /// <c>CanvasInteractionViewModel.SetPickAnalysisOutputModeCommand</c>.
    /// </summary>
    public Action? PickRequested { get; set; }

    /// <summary>Initializes a new instance of <see cref="AnalysisOutputPanelViewModel"/>.</summary>
    public AnalysisOutputPanelViewModel()
    {
        // Live language switch: the "(automatic)" placeholder must re-translate.
        LocalizationService.Instance.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Wires the panel to the active design canvas.</summary>
    /// <param name="canvas">Canvas providing components and the designation.</param>
    public void Configure(DesignCanvasViewModel canvas)
    {
        if (_canvas != null)
        {
            _canvas.AnalysisOutput.PropertyChanged -= OnDesignationChanged;
            _canvas.Components.CollectionChanged -= OnComponentsChanged;
        }
        _canvas = canvas;
        canvas.AnalysisOutput.PropertyChanged += OnDesignationChanged;
        canvas.Components.CollectionChanged += OnComponentsChanged;
        Refresh();
    }

    /// <summary>Activates the eyedropper picker on the canvas.</summary>
    [RelayCommand]
    private void Pick() => PickRequested?.Invoke();

    /// <summary>Clears the designation; the analyses fall back to automatic selection.</summary>
    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void Clear() => _canvas?.AnalysisOutput.Clear();

    private void OnDesignationChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnComponentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>
    /// Re-reads the designation. A designation whose component was deleted is pruned
    /// here (the collection-changed hook fires on delete), so the display and the
    /// stored state can never disagree.
    /// </summary>
    private void Refresh()
    {
        var designatedId = _canvas?.AnalysisOutput.CouplerId;
        var coupler = designatedId == null
            ? null
            : _canvas!.Components.FirstOrDefault(c => c.Component.Id == designatedId.Value);

        if (designatedId != null && coupler == null)
        {
            _canvas!.AnalysisOutput.Clear();
            return; // Clear() re-enters Refresh via OnDesignationChanged.
        }

        HasOutput = coupler != null;
        OutputDisplayName = coupler?.Name
            ?? LocalizationService.Instance.Translate("Analysis.Output.None");
    }
}

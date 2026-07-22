using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>Bottom analysis dock: collapsible host for the Transient and Eye/BER tabs (#570/#535).</summary>
public partial class AnalysisDockViewModel : ObservableObject
{
    /// <summary>Transient (time-domain) analysis tab.</summary>
    public TimeDomainViewModel Transient { get; }

    /// <summary>Eye-diagram / BER analysis tab.</summary>
    public EyeDiagramViewModel Eye { get; }

    /// <summary>Shared analysis-output header (#754): shows/picks/clears THE output coupler.</summary>
    public AnalysisOutputPanelViewModel Output { get; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>Minimum dock content height (px) when dragging the resize grip.</summary>
    public const double MinDockHeight = 120;

    /// <summary>Maximum dock content height (px) when dragging the resize grip.</summary>
    public const double MaxDockHeight = 640;

    /// <summary>User-adjustable height (px) of the dock's content area; driven by the resize grip.</summary>
    [ObservableProperty]
    private double _dockHeight = 260;

    /// <summary>Sets <see cref="DockHeight"/> to <paramref name="height"/>, clamped to
    /// [<see cref="MinDockHeight"/>, <see cref="MaxDockHeight"/>].</summary>
    public void SetDockHeight(double height) =>
        DockHeight = System.Math.Clamp(height, MinDockHeight, MaxDockHeight);

    /// <summary>Initializes a new instance of <see cref="AnalysisDockViewModel"/>.</summary>
    /// <param name="transient">Transient (time-domain) analysis tab ViewModel.</param>
    /// <param name="eye">Eye-diagram / BER analysis tab ViewModel.</param>
    /// <param name="output">Shared analysis-output header ViewModel (#754).</param>
    public AnalysisDockViewModel(
        TimeDomainViewModel transient, EyeDiagramViewModel eye, AnalysisOutputPanelViewModel output)
    {
        Transient = transient;
        Eye = eye;
        Output = output;
    }

    /// <summary>Wires both tabs and the shared output header to the active design canvas.</summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        Transient.Configure(canvas);
        Eye.Configure(canvas);
        Output.Configure(canvas);
    }

    /// <summary>Opens the dock on the Transient tab (called when Run is invoked in Transient mode).</summary>
    public void OpenTransient()
    {
        SelectedTabIndex = 0;
        IsVisible = true;
    }

    /// <summary>Toggles the dock's visibility.</summary>
    [RelayCommand]
    private void Toggle() => IsVisible = !IsVisible;
}

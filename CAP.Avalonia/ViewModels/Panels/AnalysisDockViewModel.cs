using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.ViewModels.Analysis;
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

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>Initializes a new instance of <see cref="AnalysisDockViewModel"/>.</summary>
    /// <param name="transient">Transient (time-domain) analysis tab ViewModel.</param>
    /// <param name="eye">Eye-diagram / BER analysis tab ViewModel.</param>
    public AnalysisDockViewModel(TimeDomainViewModel transient, EyeDiagramViewModel eye)
    {
        Transient = transient;
        Eye = eye;
    }

    /// <summary>Wires both tabs to the active design canvas.</summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        Transient.Configure(canvas);
        Eye.Configure(canvas);
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

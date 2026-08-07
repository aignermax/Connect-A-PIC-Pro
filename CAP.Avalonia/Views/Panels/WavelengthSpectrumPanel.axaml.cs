using Avalonia.Controls;
using CAP.Avalonia.Controls.Plotting;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Panel for the transmission-vs-wavelength spectrum plot (#816).
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class WavelengthSpectrumPanel : UserControl
{
    /// <summary>Initializes the WavelengthSpectrumPanel.</summary>
    public WavelengthSpectrumPanel()
    {
        InitializeComponent();
        // Plain wheel scrolls the analysis dock; Ctrl(/Cmd)+wheel zooms the plot.
        SpectrumPlot.Controller = ScrollFriendlyPlotController.Create();
    }
}

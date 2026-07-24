using Avalonia.Controls;
using CAP.Avalonia.Controls.Plotting;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Panel for time-domain (transient) simulation via IFFT of S-parameters.
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class TimeDomainPanel : UserControl
{
    /// <summary>Initializes the TimeDomainPanel.</summary>
    public TimeDomainPanel()
    {
        InitializeComponent();
        // #693: plain wheel scrolls the analysis dock; Ctrl(/Cmd)+wheel zooms the plot.
        WaveformPlot.Controller = ScrollFriendlyPlotController.Create();
    }
}

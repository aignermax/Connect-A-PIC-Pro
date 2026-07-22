using Avalonia.Controls;
using CAP.Avalonia.Controls.Plotting;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Panel for eye-diagram / BER analysis of the transient simulation (#535).
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class EyeDiagramPanel : UserControl
{
    /// <summary>Initializes the EyeDiagramPanel.</summary>
    public EyeDiagramPanel()
    {
        InitializeComponent();
        // #693: plain wheel scrolls the analysis dock; Ctrl(/Cmd)+wheel zooms the plot.
        EyePlot.Controller = ScrollFriendlyPlotController.Create();
    }
}

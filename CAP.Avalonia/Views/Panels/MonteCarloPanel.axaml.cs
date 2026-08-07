using Avalonia.Controls;
using CAP.Avalonia.Controls.Plotting;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Panel for the Monte-Carlo fabrication-variance analysis (#818).
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class MonteCarloPanel : UserControl
{
    /// <summary>Initializes the MonteCarloPanel.</summary>
    public MonteCarloPanel()
    {
        InitializeComponent();
        // Plain wheel scrolls the analysis dock; Ctrl(/Cmd)+wheel zooms the plot (#693).
        MonteCarloPlot.Controller = ScrollFriendlyPlotController.Create();
    }
}

using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Panel for viewing/exporting the design's gdsfactory YAML netlist (issue #687).
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class NetlistPanel : UserControl
{
    /// <summary>Initializes the NetlistPanel.</summary>
    public NetlistPanel()
    {
        InitializeComponent();
    }
}

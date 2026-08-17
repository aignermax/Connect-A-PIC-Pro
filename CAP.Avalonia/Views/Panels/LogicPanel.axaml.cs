using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Collapsible right-panel section that assembles the logic network of the loaded
/// design and shows live gate outputs while the user toggles the network inputs.
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class LogicPanel : UserControl
{
    /// <summary>Initializes the LogicPanel.</summary>
    public LogicPanel()
    {
        InitializeComponent();
    }
}

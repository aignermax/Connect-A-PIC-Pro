using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Collapsible right-panel section that shows the truth table of the selected
/// component group (issue #947). DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class TruthTablePanel : UserControl
{
    /// <summary>Initializes the TruthTablePanel.</summary>
    public TruthTablePanel()
    {
        InitializeComponent();
    }
}

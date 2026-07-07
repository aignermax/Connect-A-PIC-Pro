using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Collapsible bottom dock hosting the Transient and Eye/BER analysis tabs (#570/#535).
/// DataContext is inherited from MainWindow (MainViewModel).
/// </summary>
public partial class AnalysisDockPanel : UserControl
{
    /// <summary>Initializes the AnalysisDockPanel.</summary>
    public AnalysisDockPanel()
    {
        InitializeComponent();
    }
}

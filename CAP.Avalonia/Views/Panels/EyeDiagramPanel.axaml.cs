using Avalonia.Controls;

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
    }
}

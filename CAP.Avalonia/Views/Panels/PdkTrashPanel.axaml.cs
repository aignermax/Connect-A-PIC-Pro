using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Compact flyout content for the PDK-Management trash: lists recoverable deleted PDKs and
/// removed components with a Restore action (old items expire automatically). DataContext is a
/// <c>PdkTrashViewModel</c>.
/// </summary>
public partial class PdkTrashPanel : UserControl
{
    /// <summary>Initialises the panel.</summary>
    public PdkTrashPanel()
    {
        InitializeComponent();
    }
}

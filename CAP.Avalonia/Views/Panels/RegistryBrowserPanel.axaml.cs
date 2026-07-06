using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Right-panel section that browses the open photonic component registry
/// (issue #656). Bound to <c>RightPanel.Registry</c> on the MainViewModel.
/// </summary>
public partial class RegistryBrowserPanel : UserControl
{
    /// <summary>Initializes the panel from AXAML.</summary>
    public RegistryBrowserPanel()
    {
        InitializeComponent();
    }
}

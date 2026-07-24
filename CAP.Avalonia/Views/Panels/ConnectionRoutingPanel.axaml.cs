using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Right-panel section with per-connection routing options (issue #574):
/// routing style, width/bend radius, route freezing and manual per-bend radius.
/// Binds to <see cref="CAP.Avalonia.ViewModels.MainViewModel"/> like the other panels.
/// </summary>
public partial class ConnectionRoutingPanel : UserControl
{
    /// <summary>Initializes a new instance of <see cref="ConnectionRoutingPanel"/>.</summary>
    public ConnectionRoutingPanel()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Code-behind of the Imported Layers panel (issue #858): per-layer show/hide
/// and opacity controls for imported GDS geometry.
/// </summary>
public partial class GdsLayerVisibilityPanel : UserControl
{
    /// <summary>Initializes the panel.</summary>
    public GdsLayerVisibilityPanel()
    {
        InitializeComponent();
    }
}

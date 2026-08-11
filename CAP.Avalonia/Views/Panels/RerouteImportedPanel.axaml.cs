using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Right-panel section for re-routing imported (frozen) waveguide routes (issue #857):
/// shows how many frozen imported routes the design holds, re-routes them (all or the
/// selected one) with the live router as one undoable action, and reports the
/// before/after length and bend delta.
/// Binds to <see cref="CAP.Avalonia.ViewModels.MainViewModel"/> like the other panels.
/// </summary>
public partial class RerouteImportedPanel : UserControl
{
    /// <summary>Initializes a new instance of <see cref="RerouteImportedPanel"/>.</summary>
    public RerouteImportedPanel()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Right-panel section for length matching of a single selected waveguide connection:
/// shows the current path length and lets the user apply or clear an exact target length.
/// Binds to <see cref="CAP.Avalonia.ViewModels.MainViewModel"/> like the other panels.
/// </summary>
public partial class LengthMatchingPanel : UserControl
{
    /// <summary>Initializes a new instance of <see cref="LengthMatchingPanel"/>.</summary>
    public LengthMatchingPanel()
    {
        InitializeComponent();
    }
}

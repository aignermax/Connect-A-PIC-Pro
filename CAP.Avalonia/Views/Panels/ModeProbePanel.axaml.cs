using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Non-modal mode-slice probe flyout (issue #691), positioned at the canvas click
/// point by the overlay canvas in <c>MainWindow.axaml</c>. All logic lives in
/// <see cref="ViewModels.Solvers.ModeProbe.ModeProbeViewModel"/>.
/// </summary>
public partial class ModeProbePanel : UserControl
{
    /// <summary>Initialises the panel.</summary>
    public ModeProbePanel()
    {
        InitializeComponent();
    }
}

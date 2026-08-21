using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Non-modal card for the "Watch it compute" tour (issue #1143), anchored at the
/// bottom centre of the canvas overlay in <c>MainWindow.axaml</c>. All logic lives
/// in <see cref="ViewModels.Onboarding.FirstStepsTutorial.WatchComputeTourViewModel"/>.
/// </summary>
public partial class WatchComputeTourPanel : UserControl
{
    /// <summary>Initialises the panel.</summary>
    public WatchComputeTourPanel()
    {
        InitializeComponent();
    }
}

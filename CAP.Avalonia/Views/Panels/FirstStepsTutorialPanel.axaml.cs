using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Non-modal card for the guided first-steps tour (issue #1080), anchored at the
/// bottom centre of the canvas overlay in <c>MainWindow.axaml</c>. All logic lives
/// in <see cref="ViewModels.Onboarding.FirstStepsTutorial.TutorialViewModel"/>.
/// </summary>
public partial class FirstStepsTutorialPanel : UserControl
{
    /// <summary>Initialises the panel.</summary>
    public FirstStepsTutorialPanel()
    {
        InitializeComponent();
    }
}

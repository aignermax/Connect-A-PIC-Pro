using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Plain-language explainer for the Eye/BER panel: how an eye diagram is
/// built from bit-length waveform slices, why the eye opening matters, and
/// what Q factor / BER / threshold mean — with a small looping animation.
/// Opened from the panel's "?" button (same pattern as
/// <see cref="TransientHelpFlyout"/>).
/// </summary>
public partial class EyeHelpFlyout : UserControl
{
    /// <summary>Initializes the flyout content.</summary>
    public EyeHelpFlyout()
    {
        InitializeComponent();
    }
}

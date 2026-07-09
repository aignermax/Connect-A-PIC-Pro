using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Plain-language explainer for the transient panel: what the simulation does,
/// why it exists, what the three signal sources are for, and how to read the
/// plot — with a small looping animation. Opened from the panel's "?" button.
/// </summary>
public partial class TransientHelpFlyout : UserControl
{
    /// <summary>Initializes the flyout content.</summary>
    public TransientHelpFlyout()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;

namespace CAP.Avalonia.Views;

/// <summary>
/// Window for authoring a new PDK component: render its geometry, optionally recompute its
/// S-matrix via FDTD, and save it into a fabrication process's user PDK. DataContext must be a
/// <see cref="CAP.Avalonia.ViewModels.Components.AddCustomComponent.NewComponentViewModel"/>.
/// </summary>
public partial class NewComponentWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public NewComponentWindow()
    {
        InitializeComponent();
    }
}

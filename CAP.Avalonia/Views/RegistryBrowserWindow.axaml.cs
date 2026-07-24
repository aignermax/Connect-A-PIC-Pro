using Avalonia.Controls;
using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

namespace CAP.Avalonia.Views;

/// <summary>
/// Non-modal "Component Registry" tool window (issue #656): a full-size,
/// resizable browser for the open photonic component registry with a tile
/// grid, a filter bar (search / process / status / refresh) and a detail
/// column. Opened from the Component Library header and the Tools flyout;
/// <see cref="MainWindow"/> deduplicates so a second open activates the
/// existing window. The registry index is lazy-loaded on first open.
/// </summary>
public partial class RegistryBrowserWindow : Window
{
    /// <summary>Initializes the window and wires the lazy first index load.</summary>
    public RegistryBrowserWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as RegistryBrowserViewModel)?.EnsureLoaded();
    }
}

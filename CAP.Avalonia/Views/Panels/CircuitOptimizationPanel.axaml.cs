using Avalonia.Controls;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Right-panel section for circuit optimization (issue #820): configures the
/// objective, budget and seed, runs the search, and lists the top-N improved
/// variants with one-click, undo-safe apply.
/// Binds to <see cref="CAP.Avalonia.ViewModels.MainViewModel"/> like the other panels.
/// </summary>
public partial class CircuitOptimizationPanel : UserControl
{
    /// <summary>Initializes a new instance of <see cref="CircuitOptimizationPanel"/>.</summary>
    public CircuitOptimizationPanel()
    {
        InitializeComponent();
    }
}

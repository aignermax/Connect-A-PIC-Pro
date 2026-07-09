using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CAP.Avalonia.ViewModels;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Collapsible bottom dock hosting the Transient and Eye/BER analysis tabs (#570/#535).
/// DataContext is inherited from MainWindow (MainViewModel). The top edge carries a resize
/// grip that drags the dock's content height (#570 field feedback).
/// </summary>
public partial class AnalysisDockPanel : UserControl
{
    private bool _resizing;
    private double _startPointerY;
    private double _startHeight;

    /// <summary>Initializes the AnalysisDockPanel.</summary>
    public AnalysisDockPanel()
    {
        InitializeComponent();
    }

    private ViewModels.Panels.AnalysisDockViewModel? Dock =>
        (DataContext as MainViewModel)?.BottomPanel?.Analysis;

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var dock = Dock;
        if (dock == null || this.GetVisualRoot() is not Visual root) return;
        _resizing = true;
        _startPointerY = e.GetPosition(root).Y;
        _startHeight = dock.DockHeight;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || Dock is not { } dock || this.GetVisualRoot() is not Visual root) return;
        // The grip sits at the dock's top edge; dragging up (smaller Y) grows the dock.
        var currentY = e.GetPosition(root).Y;
        dock.SetDockHeight(_startHeight - (currentY - _startPointerY));
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}

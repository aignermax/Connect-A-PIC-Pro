using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CAP.Avalonia.Controls.Handlers;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.Gestures;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls;

/// <summary>
/// Design canvas control — a lean coordinator that delegates rendering to renderer objects
/// and input handling to <see cref="KeyboardHandler"/> and gesture recognizers.
/// No partial classes; all behavior is composed via the Strategy pattern.
/// </summary>
public class DesignCanvas : Control
{
    // ── Styled Properties ──────────────────────────────────────────────────

    /// <summary>Avalonia styled property for the canvas ViewModel.</summary>
    public static readonly StyledProperty<DesignCanvasViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<DesignCanvas, DesignCanvasViewModel?>(nameof(ViewModel));

    /// <summary>Avalonia styled property for the zoom level.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DesignCanvas, double>(nameof(Zoom), 1.0);

    /// <summary>Avalonia styled property for the main application ViewModel.</summary>
    public static readonly StyledProperty<MainViewModel?> MainViewModelProperty =
        AvaloniaProperty.Register<DesignCanvas, MainViewModel?>(nameof(MainViewModel));

    /// <summary>Gets or sets the canvas ViewModel.</summary>
    public DesignCanvasViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Gets or sets the current zoom level.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Gets or sets the main application ViewModel.</summary>
    public MainViewModel? MainViewModel
    {
        get => GetValue(MainViewModelProperty);
        set => SetValue(MainViewModelProperty, value);
    }

    // ── Internal State ─────────────────────────────────────────────────────

    private readonly CanvasInteractionState _interactionState = new();

    /// <summary>Gets the last canvas position tracked by pointer movement. Used for paste-at-cursor.</summary>
    public Point LastCanvasPosition => _interactionState.LastCanvasPosition;

    // ── Renderers ──────────────────────────────────────────────────────────

    private readonly GridRenderer _gridRenderer;
    private readonly PathfindingOverlayRenderer _pathfindingOverlayRenderer;
    private readonly WaveguideConnectionRenderer _waveguideConnectionRenderer;
    private readonly ComponentRenderer _componentRenderer;
    private readonly PreviewRenderer _previewRenderer;
    private readonly CanvasOverlayRenderer _overlayRenderer;

    // ── Input Handlers ─────────────────────────────────────────────────────

    private readonly KeyboardHandler _keyboardHandler;
    private List<IGestureRecognizer> _gestureRecognizers = [];
    private IGestureRecognizer? _activeGesture;

    // ── Constructor ────────────────────────────────────────────────────────

    static DesignCanvas()
    {
        AffectsRender<DesignCanvas>(ViewModelProperty, ZoomProperty);
        MainViewModelProperty.Changed.AddClassHandler<DesignCanvas>((c, e) => c.OnMainViewModelChanged(e));
        ViewModelProperty.Changed.AddClassHandler<DesignCanvas>((c, e) => c.OnViewModelChanged(e));
    }

    /// <summary>Initializes a new instance of <see cref="DesignCanvas"/>.</summary>
    public DesignCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        _gridRenderer = new GridRenderer();
        _pathfindingOverlayRenderer = new PathfindingOverlayRenderer();
        _waveguideConnectionRenderer = new WaveguideConnectionRenderer();
        _componentRenderer = new ComponentRenderer();
        _previewRenderer = new PreviewRenderer();
        _overlayRenderer = new CanvasOverlayRenderer();
        _keyboardHandler = new KeyboardHandler(() => ViewModel, () => MainViewModel, () => Bounds);

        InitGestures();

        // Select the component under the cursor when the context menu opens, so the menu acts on the
        // right-clicked element. Tunnel phase runs before the menu evaluates its command CanExecute.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    /// <summary>Renders the canvas by orchestrating all registered renderers in layer order.</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var vm = ViewModel;
        if (vm == null) return;

        var rc = new CanvasRenderContext
        {
            ViewModel = vm,
            MainViewModel = MainViewModel,
            InteractionState = _interactionState,
            Zoom = Zoom,
            Bounds = Bounds,
            GdsPreviewRenderService = MainViewModel?.GdsPreviewRenderService
        };

        context.FillRectangle(Brushes.Black, Bounds);
        _gridRenderer.RenderBackground(context, rc);

        using (context.PushTransform(Matrix.CreateTranslation(vm.PanX, vm.PanY)))
        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom)))
        {
            _gridRenderer.RenderWorld(context, rc);
            _pathfindingOverlayRenderer.Render(context, rc);
            _waveguideConnectionRenderer.Render(context, rc);
            _componentRenderer.Render(context, rc);
            _previewRenderer.Render(context, rc);
        }

        _overlayRenderer.Render(context, rc);
    }

    // ── Mouse Input (delegates to gesture recognizers) ─────────────────────

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        _interactionState.LastPointerPosition = point;
        var vm = ViewModel;
        if (vm == null) return;
        var canvasPoint = ScreenToCanvas(point);
        _activeGesture = null;
        foreach (var recognizer in _gestureRecognizers)
        {
            if (recognizer.TryRecognize(e, canvasPoint, vm, MainViewModel))
            {
                _activeGesture = recognizer;
                break;
            }
        }
        e.Handled = true;
        Focus();
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var delta = point - _interactionState.LastPointerPosition;
        var vm = ViewModel;
        if (vm == null) return;
        var canvasPoint = ScreenToCanvas(point);
        _interactionState.LastCanvasPosition = canvasPoint;
        foreach (var recognizer in _gestureRecognizers)
            recognizer.UpdatePassiveState(canvasPoint, vm, MainViewModel);
        _activeGesture?.OnPointerMoved(e, delta, canvasPoint, vm, MainViewModel);
        _interactionState.LastPointerPosition = point;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_interactionState.HasPanned && e.InitialPressMouseButton == MouseButton.Right)
        {
            e.Handled = true;
            _interactionState.HasPanned = false;
            _interactionState.IsPanning = false;
            _activeGesture = null;
            return;
        }
        base.OnPointerReleased(e);
        if (ViewModel != null)
            _activeGesture?.OnPointerReleased(e, ViewModel, MainViewModel);
        _activeGesture = null;
    }

    /// <summary>
    /// Selects the component under the cursor before the context menu opens so its actions
    /// (Component Settings, Copy, Delete, …) operate on the right-clicked element. A keyboard-invoked
    /// menu provides no position; in that case the current selection is kept.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var mainVm = MainViewModel;
        if (mainVm == null) return;
        if (!e.TryGetPosition(this, out var screenPoint)) return;
        var canvasPoint = ScreenToCanvas(screenPoint);
        mainVm.CanvasInteraction.SelectComponentAt(canvasPoint.X, canvasPoint.Y);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = e.Delta.Y > 0 ? 1.1 : 0.9;
        var newZoom = Math.Clamp(Zoom * delta, 0.1, 10.0);
        var point = e.GetPosition(this);
        var vm = ViewModel;
        if (vm != null)
        {
            var beforeZoom = ScreenToCanvas(point);
            Zoom = newZoom;
            var afterZoom = ScreenToCanvas(point);
            vm.PanX += (afterZoom.X - beforeZoom.X) * Zoom;
            vm.PanY += (afterZoom.Y - beforeZoom.Y) * Zoom;
        }
        else
        {
            Zoom = newZoom;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keyboardHandler.OnKeyDown(e);
        InvalidateVisual();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void InitGestures()
    {
        _gestureRecognizers =
        [
            new PanGestureRecognizer(_interactionState, InvalidateVisual),
            new ConnectionGestureRecognizer(_interactionState, InvalidateVisual),
            new PlacementGestureRecognizer(_interactionState, InvalidateVisual),
            new ComponentDragGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom, c => Cursor = c),
            new SelectionBoxGestureRecognizer(_interactionState, InvalidateVisual),
        ];
    }

    private Point ScreenToCanvas(Point screenPoint)
    {
        var vm = ViewModel;
        if (vm == null) return screenPoint;
        return new Point((screenPoint.X - vm.PanX) / Zoom, (screenPoint.Y - vm.PanY) / Zoom);
    }

    // ── ViewModel Change Handlers ──────────────────────────────────────────

    private void OnMainViewModelChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.CommandManager.StateChanged -= OnCommandStateChanged;
            oldVm.GdsPreviewRenderService.OnPreviewLoaded -= InvalidateVisual;
            oldVm.GdsPreviewRenderService.RawCodeLookup = null;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.CommandManager.StateChanged += OnCommandStateChanged;
            newVm.GdsPreviewRenderService.OnPreviewLoaded += InvalidateVisual;
            // Only Nazca-backend overrides feed the (Nazca-based) canvas renderer;
            // gdsfactory-backend code would fail there, so those instances fall
            // back to their template preview (issue #637).
            newVm.GdsPreviewRenderService.RawCodeLookup = id =>
                newVm.FileOperations.StoredNazcaOverrides.TryGetValue(id, out var o)
                && !CAP_DataAccess.Persistence.PIR.OverrideBackend.IsGdsFactory(o.Backend)
                    ? o.RawCode
                    : null;
        }
    }

    private void OnViewModelChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is DesignCanvasViewModel oldCanvas)
        {
            oldCanvas.PropertyChanged -= OnCanvasViewModelPropertyChanged;
            oldCanvas.RepaintRequested = null;
            oldCanvas.Components.CollectionChanged -= OnComponentsCollectionChanged;
            oldCanvas.Connections.CollectionChanged -= OnConnectionsCollectionChanged;
        }
        if (e.NewValue is DesignCanvasViewModel newCanvas)
        {
            newCanvas.PropertyChanged += OnCanvasViewModelPropertyChanged;
            newCanvas.RepaintRequested = () => InvalidateVisual();
            newCanvas.Components.CollectionChanged += OnComponentsCollectionChanged;
            newCanvas.Connections.CollectionChanged += OnConnectionsCollectionChanged;
        }
    }

    private void OnComponentsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnConnectionsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnCanvasViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignCanvasViewModel.ShowPowerFlow)
            or nameof(DesignCanvasViewModel.IsRouting)
            or nameof(DesignCanvasViewModel.PanX)
            or nameof(DesignCanvasViewModel.PanY)
            or nameof(DesignCanvasViewModel.SelectedComponent))
        {
            // SelectedComponent: redraw so the highlight follows a selection made
            // outside the canvas (e.g. clicking a node in the hierarchy panel).
            InvalidateVisual();
        }
    }

    private void OnCommandStateChanged(object? sender, EventArgs e) => InvalidateVisual();
}

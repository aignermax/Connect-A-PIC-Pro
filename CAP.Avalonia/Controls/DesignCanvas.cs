using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CAP.Avalonia.Controls.Canvas.AnalysisOutput;
using CAP.Avalonia.Controls.Canvas.BendHandles;
using CAP.Avalonia.Controls.Canvas.CutTool;
using CAP.Avalonia.Controls.Canvas.SegmentShiftHandles;
using CAP.Avalonia.Controls.Handlers;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.Gestures;
using CAP.Avalonia.Services.Localization;
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

    /// <summary>Gets the shared per-canvas interaction state (renderers and gestures read/write it).</summary>
    public CanvasInteractionState InteractionState => _interactionState;

    // ── Renderers ──────────────────────────────────────────────────────────

    private readonly GridRenderer _gridRenderer;
    private readonly PathfindingOverlayRenderer _pathfindingOverlayRenderer;
    private readonly WaveguideConnectionRenderer _waveguideConnectionRenderer;
    private readonly BendHandleRenderer _bendHandleRenderer;
    private readonly SegmentShiftHandleRenderer _segmentShiftHandleRenderer;
    private readonly ComponentRenderer _componentRenderer;
    private readonly AnalysisOutputOverlayRenderer _analysisOutputRenderer;
    private readonly PreviewRenderer _previewRenderer;
    private readonly CutToolOverlayRenderer _cutToolOverlayRenderer;
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
        _bendHandleRenderer = new BendHandleRenderer();
        _segmentShiftHandleRenderer = new SegmentShiftHandleRenderer();
        _componentRenderer = new ComponentRenderer();
        _analysisOutputRenderer = new AnalysisOutputOverlayRenderer();
        _previewRenderer = new PreviewRenderer();
        _cutToolOverlayRenderer = new CutToolOverlayRenderer();
        _overlayRenderer = new CanvasOverlayRenderer();
        _keyboardHandler = new KeyboardHandler(() => ViewModel, () => MainViewModel, () => Bounds);

        InitGestures();

        // Select the component under the cursor when the context menu opens, so the menu acts on the
        // right-clicked element. Tunnel phase runs before the menu evaluates its command CanExecute.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }

    // Redraw the code-drawn HUD (mode indicator, status line) in the newly chosen language.
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => InvalidateVisual();

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
            GdsPreviewRenderService = MainViewModel?.GdsPreviewRenderService,
            LayerVisibility = MainViewModel?.LayerVisibility.State
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
            // Deferred text labels (component/pin names, connection readouts) flush AFTER all
            // component bodies and connection lines, so no geometry can ever paint over a
            // label — but BEFORE the interaction overlays/handles below, which the user
            // manipulates and which must keep winning against labels.
            rc.Labels.Flush(context, Zoom);
            // Analysis-output overlay (#754) sits on top of components so the candidate
            // glow and the designated "OUT" tag are never hidden by component fills.
            _analysisOutputRenderer.Render(context, rc);
            _previewRenderer.Render(context, rc);
            // Cut tool overlay: guide lines and insertion candidates sit above
            // components and waveguides so the clickable markers are never obscured.
            _cutToolOverlayRenderer.Render(context, rc);
            // Handles draw last so they sit on top of the routed path and components; segment
            // midpoint handles go under the bend handles, matching the gesture priority (#791).
            _segmentShiftHandleRenderer.Render(context, rc);
            _bendHandleRenderer.Render(context, rc);
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

    /// <summary>
    /// Clears the simple-component hover once the pointer leaves the canvas entirely — without
    /// this, the last-hovered component would keep winning its name-label overlap priority (and
    /// a stale reference would linger in <see cref="CanvasInteractionState"/>) until the pointer
    /// re-entered and moved again.
    /// </summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_interactionState.HoveredComponent == null) return;
        _interactionState.HoveredComponent = null;
        InvalidateVisual();
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
        var newZoom = Math.Clamp(Zoom * delta, CanvasZoomLimits.Min, CanvasZoomLimits.Max);
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
            // First: a bend-radius handle grab must win over selection / component drag (#574).
            new BendHandleGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom),
            // Segment midpoint handles rank right below bend handles (#791): a bend grab wins
            // when the two handles overlap, but both win over selection / component drag.
            new SegmentShiftGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom),
            new PanGestureRecognizer(_interactionState, InvalidateVisual),
            new CutToolGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom),
            new ConnectionGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom),
            new PlacementGestureRecognizer(_interactionState, InvalidateVisual),
            new ComponentDragGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom, c => Cursor = c),
            new SelectionBoxGestureRecognizer(_interactionState, InvalidateVisual, () => Zoom),
            new HoverHighlightGestureRecognizer(_interactionState, InvalidateVisual),
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
            oldVm.CanvasInteraction.PropertyChanged -= OnInteractionPropertyChanged;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.CommandManager.StateChanged += OnCommandStateChanged;
            newVm.GdsPreviewRenderService.OnPreviewLoaded += InvalidateVisual;
            newVm.CanvasInteraction.PropertyChanged += OnInteractionPropertyChanged;
        }
    }

    /// <summary>
    /// Repaints when the interaction mode changes so mode-dependent overlays (the
    /// analysis-output candidate glow #754, the HUD mode indicator) update immediately
    /// after a mode button click instead of on the next pointer movement.
    /// </summary>
    private void OnInteractionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Panels.CanvasInteractionViewModel.CurrentMode))
            InvalidateVisual();
    }

    private void OnViewModelChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is DesignCanvasViewModel oldCanvas)
        {
            oldCanvas.PropertyChanged -= OnCanvasViewModelPropertyChanged;
            oldCanvas.RepaintRequested = null;
            oldCanvas.Components.CollectionChanged -= OnComponentsCollectionChanged;
            oldCanvas.Connections.CollectionChanged -= OnConnectionsCollectionChanged;
            oldCanvas.AnalysisOutput.PropertyChanged -= OnAnalysisOutputChanged;
        }
        if (e.NewValue is DesignCanvasViewModel newCanvas)
        {
            newCanvas.PropertyChanged += OnCanvasViewModelPropertyChanged;
            newCanvas.RepaintRequested = () => InvalidateVisual();
            newCanvas.Components.CollectionChanged += OnComponentsCollectionChanged;
            newCanvas.Connections.CollectionChanged += OnConnectionsCollectionChanged;
            newCanvas.AnalysisOutput.PropertyChanged += OnAnalysisOutputChanged;
        }
    }

    /// <summary>Repaints when the designated analysis output changes (#754) so the "OUT" tag follows.</summary>
    private void OnAnalysisOutputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => InvalidateVisual();

    private void OnComponentsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnConnectionsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnCanvasViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesignCanvasViewModel.ShowPowerFlow)
            or nameof(DesignCanvasViewModel.IsSimulationModeActive)
            or nameof(DesignCanvasViewModel.IsRouting)
            or nameof(DesignCanvasViewModel.PanX)
            or nameof(DesignCanvasViewModel.PanY)
            or nameof(DesignCanvasViewModel.SelectedComponent)
            or nameof(DesignCanvasViewModel.ActiveProcessLabel))
        {
            // SelectedComponent: redraw so the highlight follows a selection made
            // outside the canvas (e.g. clicking a node in the hierarchy panel).
            InvalidateVisual();
        }
    }

    private void OnCommandStateChanged(object? sender, EventArgs e) => InvalidateVisual();
}

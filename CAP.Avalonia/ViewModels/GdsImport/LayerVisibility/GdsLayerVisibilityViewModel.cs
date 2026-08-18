using System.Collections.ObjectModel;
using Avalonia.Threading;
using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport.LayerVisibility;

/// <summary>
/// ViewModel of the Imported Layers panel (issue #858): lists the GDS
/// (layer, datatype) pairs present on the canvas with per-layer show/hide
/// toggles and opacity sliders. Edits go into <see cref="State"/> — a pure
/// view filter the canvas renderers consult, persisted per design in the .lun.
/// </summary>
public partial class GdsLayerVisibilityViewModel : ObservableObject
{
    /// <summary>Full opacity in the percent scale the sliders use.</summary>
    private const double FullOpacityPercent = 100.0;

    private readonly DesignCanvasViewModel _canvas;
    private bool _refreshQueued;

    /// <summary>True while the canvas carries any imported per-layer geometry.</summary>
    [ObservableProperty]
    private bool _hasLayers;

    /// <summary>Initializes the panel over the given canvas and tracks its content.</summary>
    public GdsLayerVisibilityViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        _canvas.Components.CollectionChanged += (_, _) => QueueRefresh();
        _canvas.CanvasFrozenPaths.CollectionChanged += (_, _) => QueueRefresh();
        State.Changed += () => _canvas.RepaintRequested?.Invoke();
    }

    /// <summary>The per-layer display state the canvas renderers consult.</summary>
    public GdsLayerVisibilityState State { get; } = new();

    /// <summary>One row per (layer, datatype) pair present on the canvas.</summary>
    public ObservableCollection<GdsLayerVisibilityRowViewModel> Rows { get; } = new();

    /// <summary>
    /// Raised after the user edits a toggle/slider — MainViewModel wires this to
    /// the dirty flag so the per-design settings get saved.
    /// </summary>
    public Action? SettingsEdited { get; set; }

    /// <summary>Rebuilds the rows from the canvas contents and the current state.</summary>
    public void Refresh()
    {
        var usages = DesignLayerUsageCollector.Collect(
            _canvas.Components.Select(vm => vm.Component),
            _canvas.CanvasFrozenPaths);
        Rows.Clear();
        foreach (var usage in usages)
        {
            var stored = State.Get(usage.Layer, usage.DataType);
            Rows.Add(new GdsLayerVisibilityRowViewModel(
                usage.Layer, usage.DataType, usage.ShapeCount,
                stored?.IsVisible ?? true,
                (stored?.Opacity ?? 1.0) * FullOpacityPercent,
                OnRowEdited));
        }
        HasLayers = Rows.Count > 0;
    }

    /// <summary>The non-default settings for the .lun file (null when all-default).</summary>
    public List<GdsLayerVisibilityData>? CaptureForSave() => State.CaptureForSave();

    /// <summary>Restores settings from a loaded .lun file and rebuilds the rows.</summary>
    public void Restore(IEnumerable<GdsLayerVisibilityData>? entries)
    {
        State.Restore(entries);
        Refresh();
    }

    /// <summary>Resets to all-visible for a fresh design (File → New).</summary>
    public void ClearForNewDesign()
    {
        State.Clear();
        Refresh();
    }

    /// <summary>Resets every layer to fully visible.</summary>
    [RelayCommand]
    private void ShowAllLayers()
    {
        if (State.CaptureForSave() == null)
            return;
        State.Clear();
        Refresh();
        SettingsEdited?.Invoke();
    }

    private void OnRowEdited(GdsLayerVisibilityRowViewModel row)
    {
        State.Set(row.Layer, row.DataType, row.IsVisible, row.OpacityPercent / FullOpacityPercent);
        SettingsEdited?.Invoke();
    }

    /// <summary>
    /// Coalesces bursts of component changes (a large import adds hundreds of
    /// components) into one row rebuild on the UI thread; falls back to an
    /// immediate rebuild when no dispatcher is running (headless tests).
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                _refreshQueued = false;
                Refresh();
            });
        }
        catch
        {
            _refreshQueued = false;
            Refresh();
        }
    }
}

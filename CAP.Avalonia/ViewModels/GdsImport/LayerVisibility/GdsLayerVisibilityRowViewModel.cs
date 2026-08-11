using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.GdsImport.LayerVisibility;

/// <summary>
/// One row of the Imported Layers panel (issue #858): a GDS (layer, datatype)
/// pair with its show/hide toggle and opacity slider. Edits are pushed to the
/// owning <see cref="GdsLayerVisibilityViewModel"/> via a callback.
/// </summary>
public partial class GdsLayerVisibilityRowViewModel : ObservableObject
{
    private readonly Action<GdsLayerVisibilityRowViewModel> _onSettingChanged;

    /// <summary>Whether geometry on this layer is drawn on the canvas.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Draw opacity in percent (0–100) applied while the layer is visible.</summary>
    [ObservableProperty]
    private double _opacityPercent;

    /// <summary>
    /// Initializes a row with its current settings. Initial values are written to
    /// the backing fields directly, so constructing a row never fires the callback.
    /// </summary>
    public GdsLayerVisibilityRowViewModel(
        int layer, int dataType, int shapeCount,
        bool isVisible, double opacityPercent,
        Action<GdsLayerVisibilityRowViewModel> onSettingChanged)
    {
        Layer = layer;
        DataType = dataType;
        ShapeCount = shapeCount;
        _isVisible = isVisible;
        _opacityPercent = opacityPercent;
        _onSettingChanged = onSettingChanged;
    }

    /// <summary>GDS layer number.</summary>
    public int Layer { get; }

    /// <summary>GDS datatype.</summary>
    public int DataType { get; }

    /// <summary>Imported shapes (outline polygons + tagged frozen paths) on the pair.</summary>
    public int ShapeCount { get; }

    /// <summary>Row label, e.g. "Layer 11/0".</summary>
    public string DisplayName => $"Layer {Layer}/{DataType}";

    /// <summary>Shape-count badge, e.g. "(42)".</summary>
    public string ShapeCountText => $"({ShapeCount})";

    partial void OnIsVisibleChanged(bool value) => _onSettingChanged(this);

    partial void OnOpacityPercentChanged(double value) => _onSettingChanged(this);
}

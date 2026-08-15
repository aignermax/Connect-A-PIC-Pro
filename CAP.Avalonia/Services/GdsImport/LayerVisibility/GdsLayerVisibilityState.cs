namespace CAP.Avalonia.Services.GdsImport.LayerVisibility;

/// <summary>
/// Per-design view filter for imported GDS geometry (issue #858): show/hide and
/// opacity per (layer, datatype) pair. A pure display state — hiding a layer
/// never touches connectivity or simulation. Renderers query
/// <see cref="EffectiveOpacity"/> per polygon; the panel ViewModel edits entries;
/// save/load round-trips the non-default entries through the .lun file.
/// </summary>
public sealed class GdsLayerVisibilityState
{
    /// <summary>Opacities closer to 1 than this count as fully opaque (default).</summary>
    private const double OpacityEpsilon = 0.005;

    private readonly Dictionary<(int Layer, int DataType), GdsLayerVisibilityData> _overrides = new();

    /// <summary>Raised whenever any entry changes — the canvas repaints on it.</summary>
    public event Action? Changed;

    /// <summary>
    /// The draw opacity for geometry on the given pair: 0 when hidden, the stored
    /// opacity when faded, 1 for layers without an override.
    /// </summary>
    public double EffectiveOpacity(int layer, int dataType)
    {
        if (!_overrides.TryGetValue((layer, dataType), out var entry))
            return 1.0;
        return entry.IsVisible ? entry.Opacity : 0.0;
    }

    /// <summary>The stored override for a pair, or null when the layer is at its default.</summary>
    public GdsLayerVisibilityData? Get(int layer, int dataType) =>
        _overrides.TryGetValue((layer, dataType), out var entry) ? entry : null;

    /// <summary>
    /// Sets the display override for a pair. A fully-visible, fully-opaque setting
    /// removes the entry (back to default) so saves stay minimal.
    /// </summary>
    public void Set(int layer, int dataType, bool isVisible, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        bool isDefault = isVisible && opacity >= 1.0 - OpacityEpsilon;
        if (isDefault)
        {
            if (!_overrides.Remove((layer, dataType)))
                return;
        }
        else
        {
            _overrides[(layer, dataType)] = new GdsLayerVisibilityData
            {
                Layer = layer,
                DataType = dataType,
                IsVisible = isVisible,
                Opacity = opacity,
            };
        }
        Changed?.Invoke();
    }

    /// <summary>Removes all overrides (every layer back to fully visible).</summary>
    public void Clear()
    {
        if (_overrides.Count == 0)
            return;
        _overrides.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The non-default entries for the .lun file, ordered by (layer, datatype);
    /// null when every layer is at its default so the file omits the section.
    /// </summary>
    public List<GdsLayerVisibilityData>? CaptureForSave()
    {
        if (_overrides.Count == 0)
            return null;
        return _overrides.Values
            .OrderBy(e => e.Layer).ThenBy(e => e.DataType)
            .ToList();
    }

    /// <summary>
    /// Replaces all overrides with the entries loaded from a .lun file.
    /// Null/empty input resets to all-visible (files without the section).
    /// </summary>
    public void Restore(IEnumerable<GdsLayerVisibilityData>? entries)
    {
        _overrides.Clear();
        foreach (var entry in entries ?? Enumerable.Empty<GdsLayerVisibilityData>())
        {
            double opacity = Math.Clamp(entry.Opacity, 0.0, 1.0);
            if (entry.IsVisible && opacity >= 1.0 - OpacityEpsilon)
                continue;
            _overrides[(entry.Layer, entry.DataType)] = new GdsLayerVisibilityData
            {
                Layer = entry.Layer,
                DataType = entry.DataType,
                IsVisible = entry.IsVisible,
                Opacity = opacity,
            };
        }
        Changed?.Invoke();
    }
}

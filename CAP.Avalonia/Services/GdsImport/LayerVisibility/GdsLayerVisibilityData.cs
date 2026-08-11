namespace CAP.Avalonia.Services.GdsImport.LayerVisibility;

/// <summary>
/// Persisted per-design display override for one imported GDS (layer, datatype)
/// pair (issue #858). Only non-default entries (hidden or faded layers) are
/// written into the .lun file; layers without an entry render fully visible.
/// </summary>
public sealed class GdsLayerVisibilityData
{
    /// <summary>GDS layer number the override applies to.</summary>
    public int Layer { get; set; }

    /// <summary>GDS datatype the override applies to.</summary>
    public int DataType { get; set; }

    /// <summary>Whether geometry on this layer is drawn at all.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Draw opacity in [0, 1] applied when the layer is visible.</summary>
    public double Opacity { get; set; } = 1.0;
}

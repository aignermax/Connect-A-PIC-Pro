using Avalonia;
using Avalonia.Media;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Decides which simple-component name labels <see cref="ComponentRenderer"/> should draw this
/// frame: names stay visible by default for orientation (Peter's dense layouts still need to
/// tell components apart at a glance — docs/PERSONAS.md), but a label whose measured bounds
/// overlap a higher-priority one (selected &gt; hovered &gt; rest, see <see cref="LabelPriority"/>)
/// is dropped rather than drawn as illegible overlapping text.
/// ComponentGroups render their own boxed, bordered name label via
/// <see cref="ComponentGroupRenderer.RenderGroupNameLabel"/> and are out of scope here.
///
/// Two-stage caching so panning never re-triggers the expensive sweep: the priority/overlap
/// resolution (<see cref="LabelOverlapResolver"/>) depends only on quantized zoom and content
/// (position, size, rotation, name, selection, hover — everything that could change which
/// labels overlap or who wins), never on the viewport, and reruns only when that content
/// signature changes — mirroring <c>CutToolCandidateComputer</c>'s dirty flag. Per-frame
/// viewport culling is a separate, cheap step against the already-measured label bounds (not
/// the component footprint, so a label whose owner sits just outside the viewport but whose
/// text reaches into it is still drawn). Text is measured once per (name, font size) via
/// <see cref="FormattedText.SetForegroundBrush"/> reuse, so the same shaped layout is handed to
/// <see cref="ComponentRenderer"/> for drawing instead of being rebuilt there.
/// </summary>
public sealed class ComponentNameLabelComputer
{
    private const double LabelOffsetX = 5.0;
    private const double LabelOffsetY = 5.0;

    /// <summary>Zoom is quantized to this step before it factors into the overlap-resolution
    /// signature or the measured font size, so continuous zoom (e.g. a smooth scroll) doesn't
    /// force a re-measure every frame — only crossing a 5% bucket boundary does.</summary>
    private const double ZoomQuantizationStep = 0.05;

    private readonly Dictionary<(string Name, double FontSize), FormattedText> _textCache = new();

    private int? _lastOverlapSignature;
    private IReadOnlySet<Guid> _overlapVisibleIds = new HashSet<Guid>();
    private Dictionary<Guid, Rect> _labelBounds = new();
    private Dictionary<Guid, FormattedText> _labelText = new();

    /// <summary>Number of times the expensive overlap resolution actually reran — exposed so
    /// tests can verify pan/drag alone never triggers it (only zoom-bucket or content changes).</summary>
    public int RebuildCount { get; private set; }

    /// <summary>Distinct (name, font size) pairs currently held in the text-measurement cache —
    /// exposed so tests can verify unchanged components don't re-measure across rebuilds.</summary>
    public int MeasuredTextCount => _textCache.Count;

    /// <summary>
    /// Returns the <see cref="Component.Id"/> of every simple component whose name label should
    /// be drawn this frame.
    /// </summary>
    /// <param name="components">All components currently on the canvas (top-level).</param>
    /// <param name="hoveredComponentId">Id of the component currently under the pointer, if any.</param>
    /// <param name="viewportWorld">Visible canvas area in world (µm) coordinates, for culling.</param>
    /// <param name="zoom">Current canvas zoom factor.</param>
    public IReadOnlySet<Guid> GetVisibleLabelIds(
        IReadOnlyList<ComponentViewModel> components,
        Guid? hoveredComponentId,
        Rect viewportWorld,
        double zoom)
    {
        double quantizedZoom = QuantizeZoom(zoom);
        int signature = ComputeOverlapSignature(components, hoveredComponentId, quantizedZoom);
        if (signature != _lastOverlapSignature)
        {
            _lastOverlapSignature = signature;
            RebuildOverlapResolution(components, hoveredComponentId, quantizedZoom);
            RebuildCount++;
        }

        return CullByViewport(viewportWorld);
    }

    /// <summary>The pre-measured <see cref="FormattedText"/> for a visible label, so the
    /// renderer draws the exact layout this computer already shaped instead of re-measuring.</summary>
    public FormattedText? TryGetLabelText(Guid componentId) =>
        _labelText.TryGetValue(componentId, out var text) ? text : null;

    /// <summary>Cheap per-frame filter: intersects the (rare-to-change) overlap-resolved set
    /// with the current viewport against already-measured bounds — no text measurement here,
    /// so pan/zoom-drag cost is O(visible components) rect checks only.</summary>
    private HashSet<Guid> CullByViewport(Rect viewportWorld)
    {
        var visible = new HashSet<Guid>(_overlapVisibleIds.Count);
        foreach (var id in _overlapVisibleIds)
            if (_labelBounds.TryGetValue(id, out var bounds) && viewportWorld.Intersects(bounds))
                visible.Add(id);
        return visible;
    }

    private void RebuildOverlapResolution(
        IReadOnlyList<ComponentViewModel> components, Guid? hoveredComponentId, double quantizedZoom)
    {
        var candidates = new List<LabelCandidate>(components.Count);
        var bounds = new Dictionary<Guid, Rect>(components.Count);
        var text = new Dictionary<Guid, FormattedText>(components.Count);

        double fontSize = PinScreenSize.ClampWorldFontSize(PinRenderer.NameLabelFontSizeWorld, quantizedZoom);
        foreach (var comp in components)
        {
            if (comp.Component is ComponentGroup) continue;

            var id = comp.Component.Id;
            var formatted = GetOrMeasureText(comp.Name, fontSize);
            var labelBounds = new Rect(comp.X + LabelOffsetX, comp.Y + LabelOffsetY, formatted.Width, formatted.Height);
            bounds[id] = labelBounds;
            text[id] = formatted;

            var priority = comp.IsSelected ? LabelPriority.Selected
                : hoveredComponentId == id ? LabelPriority.Hovered
                : LabelPriority.Normal;
            candidates.Add(new LabelCandidate(id, labelBounds, priority));
        }

        _labelBounds = bounds;
        _labelText = text;
        _overlapVisibleIds = LabelOverlapResolver.ResolveVisibleLabels(candidates);
    }

    /// <summary>
    /// Text shaping (the expensive part of building a <see cref="FormattedText"/>) depends only
    /// on the string and font size, not colour — so a cache hit reuses the same instance across
    /// rebuilds for any component whose name and effective zoom bucket haven't changed, and the
    /// caller only needs to override the brush (see <see cref="FormattedText.SetForegroundBrush"/>)
    /// before drawing.
    /// </summary>
    private FormattedText GetOrMeasureText(string name, double fontSize)
    {
        var key = (name, fontSize);
        if (_textCache.TryGetValue(key, out var cached))
            return cached;

        var formatted = new FormattedText(name, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Arial"), fontSize, Brushes.White);
        _textCache[key] = formatted;
        return formatted;
    }

    /// <summary>Rounds to a 5% zoom bucket (floored at one step) so smooth zooming doesn't
    /// perturb the overlap-resolution signature every frame.</summary>
    private static double QuantizeZoom(double zoom)
    {
        double safeZoom = zoom <= 0 ? 1.0 : zoom;
        double quantized = Math.Round(safeZoom / ZoomQuantizationStep) * ZoomQuantizationStep;
        return Math.Max(quantized, ZoomQuantizationStep);
    }

    /// <summary>
    /// Everything that could change which labels overlap or who wins: identity, position, size,
    /// rotation, displayed name (the user-facing <see cref="ComponentViewModel.Name"/>, not just
    /// the stable <see cref="Component.Id"/> — a rename must invalidate the cached bounds/text),
    /// selection, hover, and the quantized zoom. Deliberately excludes the viewport/pan — see
    /// class remarks.
    /// </summary>
    private static int ComputeOverlapSignature(
        IReadOnlyList<ComponentViewModel> components, Guid? hoveredComponentId, double quantizedZoom)
    {
        var hash = new HashCode();
        hash.Add(quantizedZoom);
        hash.Add(hoveredComponentId);
        hash.Add(components.Count);
        foreach (var comp in components)
        {
            hash.Add(comp.Component.Id);
            hash.Add(comp.Name);
            hash.Add(comp.X);
            hash.Add(comp.Y);
            hash.Add(comp.Width);
            hash.Add(comp.Height);
            hash.Add(comp.Component.RotationDegrees);
            hash.Add(comp.IsSelected);
        }
        return hash.ToHashCode();
    }
}

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
/// <see cref="ComponentGroupRenderer.RenderGroupNameLabel"/> and are out of scope here — that
/// label already reads clearly at close spacing and groups are comparatively few, so the
/// clutter this addresses is the many small named components, not group containers.
/// The (relatively expensive) text-measurement + overlap sweep only reruns when the viewport,
/// zoom, component positions, hover, or selection actually changed since the last call — the
/// same content-signature dirty-flag <c>CutToolCandidateComputer</c> uses, since
/// <see cref="ComponentRenderer"/> calls this every frame.
/// </summary>
public sealed class ComponentNameLabelComputer
{
    private const double LabelOffsetX = 5.0;
    private const double LabelOffsetY = 5.0;

    private int? _lastSignature;
    private IReadOnlySet<string> _visibleIds = new HashSet<string>();

    /// <summary>
    /// Returns the <see cref="Component.Identifier"/> of every simple component whose name
    /// label should be drawn this frame.
    /// </summary>
    /// <param name="components">All components currently on the canvas (top-level).</param>
    /// <param name="hoveredComponent">The component currently under the pointer, if any.</param>
    /// <param name="viewportWorld">Visible canvas area in world (µm) coordinates, for culling.</param>
    /// <param name="zoom">Current canvas zoom factor.</param>
    public IReadOnlySet<string> GetVisibleLabelIds(
        IReadOnlyList<ComponentViewModel> components,
        ComponentViewModel? hoveredComponent,
        Rect viewportWorld,
        double zoom)
    {
        int signature = ComputeSignature(components, hoveredComponent, viewportWorld, zoom);
        if (signature == _lastSignature)
            return _visibleIds;

        _lastSignature = signature;
        var candidates = BuildCandidates(components, hoveredComponent, viewportWorld, zoom);
        _visibleIds = LabelOverlapResolver.ResolveVisibleLabels(candidates);
        return _visibleIds;
    }

    private static List<LabelCandidate> BuildCandidates(
        IReadOnlyList<ComponentViewModel> components,
        ComponentViewModel? hoveredComponent,
        Rect viewportWorld,
        double zoom)
    {
        var candidates = new List<LabelCandidate>(components.Count);
        if (!PinScreenSize.IsLabelReadable(PinRenderer.NameLabelFontSizeWorld, zoom))
            return candidates;

        double fontSize = PinScreenSize.CapWorldFontSize(PinRenderer.NameLabelFontSizeWorld, zoom);
        foreach (var comp in components)
        {
            if (comp.Component is ComponentGroup) continue;

            var footprint = new Rect(comp.X, comp.Y, comp.Width, comp.Height);
            if (!viewportWorld.Intersects(footprint)) continue;

            var formatted = new FormattedText(comp.Name, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), fontSize, Brushes.White);
            var bounds = new Rect(comp.X + LabelOffsetX, comp.Y + LabelOffsetY, formatted.Width, formatted.Height);

            var priority = comp.IsSelected ? LabelPriority.Selected
                : ReferenceEquals(comp, hoveredComponent) ? LabelPriority.Hovered
                : LabelPriority.Normal;

            candidates.Add(new LabelCandidate(comp.Component.Identifier, bounds, priority));
        }

        return candidates;
    }

    private static int ComputeSignature(
        IReadOnlyList<ComponentViewModel> components,
        ComponentViewModel? hoveredComponent,
        Rect viewportWorld,
        double zoom)
    {
        var hash = new HashCode();
        hash.Add(viewportWorld.X);
        hash.Add(viewportWorld.Y);
        hash.Add(viewportWorld.Width);
        hash.Add(viewportWorld.Height);
        hash.Add(zoom);
        hash.Add(hoveredComponent);
        hash.Add(components.Count);
        foreach (var comp in components)
        {
            hash.Add(comp.Component.Identifier);
            hash.Add(comp.X);
            hash.Add(comp.Y);
            hash.Add(comp.IsSelected);
        }
        return hash.ToHashCode();
    }
}

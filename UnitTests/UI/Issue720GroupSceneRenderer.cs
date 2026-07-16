using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using CAP_Core.Components.Core;
using AvaloniaCanvas = Avalonia.Controls.Canvas;

namespace UnitTests.UI;

/// <summary>
/// Draws a design-canvas replica (a placed <see cref="ComponentGroup"/> with its member
/// rectangles) for the issue #720 visual walkthrough. The real DesignCanvas needs the full
/// App DI stack and cannot be shown headless, but every group drawn here is production
/// output of <c>GroupLibraryManager</c> / <c>PlaceGroupTemplateCommand</c>.
/// Members carrying a per-instance Nazca override are highlighted.
/// </summary>
internal static class Issue720GroupSceneRenderer
{
    /// <summary>Padding added around the auto-fitted group bounds (µm).</summary>
    private const double PaddingMicrometers = 30.0;

    private static readonly Color OverrideColor = Colors.OrangeRed;
    private static readonly Color MemberFill = Color.FromRgb(0x2d, 0x4f, 0x6d);
    private static readonly Color MemberStroke = Color.FromRgb(0x6f, 0xa8, 0xdc);

    /// <summary>
    /// Renders the group's members into a fixed-size canvas; members whose identifier is in
    /// <paramref name="overriddenIds"/> are stroked orange and badged "raw-code override".
    /// </summary>
    public static AvaloniaCanvas Render(
        ComponentGroup group, ISet<string> overriddenIds, double widthPixels, double heightPixels)
    {
        var world = AutoFit(group);
        double scale = Math.Min(widthPixels / world.Width, heightPixels / world.Height);
        var canvas = new AvaloniaCanvas
        {
            Width = widthPixels,
            Height = heightPixels,
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            ClipToBounds = true,
        };

        DrawGroupBounds(canvas, group, world, scale);
        foreach (var child in group.ChildComponents)
            DrawMember(canvas, child, overriddenIds.Contains(child.Identifier), world, scale);

        return canvas;
    }

    /// <summary>World bounds enclosing all member rectangles, with padding.</summary>
    private static Rect AutoFit(ComponentGroup group)
    {
        double minX = group.ChildComponents.Min(c => c.PhysicalX);
        double minY = group.ChildComponents.Min(c => c.PhysicalY);
        double maxX = group.ChildComponents.Max(c => c.PhysicalX + c.WidthMicrometers);
        double maxY = group.ChildComponents.Max(c => c.PhysicalY + c.HeightMicrometers);
        return new Rect(
            minX - PaddingMicrometers, minY - PaddingMicrometers,
            maxX - minX + 2 * PaddingMicrometers, maxY - minY + 2 * PaddingMicrometers);
    }

    /// <summary>Draws a dashed outline plus name label around the group's member extent.</summary>
    private static void DrawGroupBounds(
        AvaloniaCanvas canvas, ComponentGroup group, Rect world, double scale)
    {
        double minX = group.ChildComponents.Min(c => c.PhysicalX) - 10;
        double minY = group.ChildComponents.Min(c => c.PhysicalY) - 10;
        double maxX = group.ChildComponents.Max(c => c.PhysicalX + c.WidthMicrometers) + 10;
        double maxY = group.ChildComponents.Max(c => c.PhysicalY + c.HeightMicrometers) + 10;

        var outline = new Rectangle
        {
            Width = (maxX - minX) * scale,
            Height = (maxY - minY) * scale,
            Stroke = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0x9a)),
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
        };
        AvaloniaCanvas.SetLeft(outline, (minX - world.X) * scale);
        AvaloniaCanvas.SetTop(outline, (minY - world.Y) * scale);
        canvas.Children.Add(outline);

        var label = new TextBlock
        {
            Text = $"Group \"{group.Name}\" at ({group.PhysicalX:0}, {group.PhysicalY:0}) µm",
            Foreground = Brushes.LightGray,
            FontSize = 12,
        };
        AvaloniaCanvas.SetLeft(label, (minX - world.X) * scale);
        AvaloniaCanvas.SetTop(label, (minY - world.Y) * scale - 18);
        canvas.Children.Add(label);
    }

    /// <summary>Draws one member as a filled rectangle with its short identifier and pins.</summary>
    private static void DrawMember(
        AvaloniaCanvas canvas, Component child, bool isOverridden, Rect world, double scale)
    {
        double left = (child.PhysicalX - world.X) * scale;
        double top = (child.PhysicalY - world.Y) * scale;

        var rect = new Rectangle
        {
            Width = Math.Max(2, child.WidthMicrometers * scale),
            Height = Math.Max(2, child.HeightMicrometers * scale),
            Fill = new SolidColorBrush(MemberFill),
            Stroke = new SolidColorBrush(isOverridden ? OverrideColor : MemberStroke),
            StrokeThickness = isOverridden ? 2.5 : 1,
        };
        AvaloniaCanvas.SetLeft(rect, left);
        AvaloniaCanvas.SetTop(rect, top);
        canvas.Children.Add(rect);

        var id = new TextBlock
        {
            Text = ShortId(child.Identifier),
            Foreground = Brushes.White,
            FontSize = 11,
        };
        AvaloniaCanvas.SetLeft(id, left + 3);
        AvaloniaCanvas.SetTop(id, top + 3);
        canvas.Children.Add(id);

        if (!isOverridden) return;
        var badge = new TextBlock
        {
            Text = "raw-code override",
            Foreground = new SolidColorBrush(OverrideColor),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
        };
        AvaloniaCanvas.SetLeft(badge, left);
        AvaloniaCanvas.SetTop(badge, top + child.HeightMicrometers * scale + 4);
        canvas.Children.Add(badge);
    }

    /// <summary>
    /// Shortens "comp_0_3f2a…" style identifiers keeping head AND tail, so two identifiers
    /// sharing a prefix (template child vs. re-keyed placed instance) remain distinguishable.
    /// </summary>
    internal static string ShortId(string identifier)
        => identifier.Length <= 22 ? identifier : $"{identifier[..10]}…{identifier[^8..]}";
}

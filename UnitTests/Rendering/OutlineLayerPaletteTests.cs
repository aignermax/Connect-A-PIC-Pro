using Avalonia.Media;
using CAP.Avalonia.Controls.Rendering;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Per-layer styling of imported geometry: the palette must keep the historical blue
/// for the waveguide core (1, 0), give different (layer, datatype) pairs different
/// muted colors deterministically, and drive the frozen-path pen selection (tagged
/// paths draw in their layer's color, untagged keep the default orange) — all
/// headless; the pixel-level proof that two layers paint differently lives in
/// <see cref="PerLayerOutlineRenderingTests"/>.
/// </summary>
public class OutlineLayerPaletteTests
{
    [Fact]
    public void ColorFor_WaveguideCore_KeepsHistoricalOutlineBlue()
    {
        OutlineLayerPalette.ColorFor(1, 0).ShouldBe(Color.FromRgb(100, 160, 220));
    }

    [Fact]
    public void ColorFor_DifferentLayers_GiveDifferentColors()
    {
        var waveguide = OutlineLayerPalette.ColorFor(1, 0);
        var metal = OutlineLayerPalette.ColorFor(11, 0);
        var extent = OutlineLayerPalette.ColorFor(111, 0);
        var unlisted = OutlineLayerPalette.ColorFor(31, 5);

        new[] { waveguide, metal, extent, unlisted }.Distinct().Count().ShouldBe(4,
            "every distinct (layer, datatype) class must be visually distinguishable");
        // The metal class runs amber: red-dominant, clearly not the waveguide blue.
        (metal.R > metal.B).ShouldBeTrue();
        (unlisted.R != waveguide.R || unlisted.G != waveguide.G || unlisted.B != waveguide.B).ShouldBeTrue();
    }

    [Fact]
    public void ColorFor_SameLayer_IsDeterministic()
    {
        OutlineLayerPalette.ColorFor(31, 5).ShouldBe(OutlineLayerPalette.ColorFor(31, 5));
        OutlineLayerPalette.ColorFor(200, 7).ShouldBe(OutlineLayerPalette.ColorFor(200, 7));
    }

    [Fact]
    public void OutlineStyleFor_KeepsHistoricalAlphaConventions()
    {
        var (fill, outline) = OutlineLayerPalette.OutlineStyleFor(11, 0);

        ((SolidColorBrush)fill).Color.ShouldBe(Color.FromArgb(46, 210, 160, 70));
        var penBrush = (SolidColorBrush)outline.Brush!;
        penBrush.Color.ShouldBe(Color.FromArgb(160, 210, 160, 70));
        outline.Thickness.ShouldBe(1.0);
    }

    [Fact]
    public void SelectFrozenPathPen_TaggedPath_DrawsInLayerColor()
    {
        var path = new FrozenWaveguidePath
        {
            Path = new RoutedPath(),
            Layer = 11,
            DataType = 0,
        };

        var pen = ComponentGroupRenderer.SelectFrozenPathPen(path);

        var brush = (SolidColorBrush)pen.Brush!;
        brush.Color.ShouldBe(Color.FromArgb(200, 210, 160, 70),
            "same alpha as the default frozen pen — only the hue carries the layer");
        pen.Thickness.ShouldBe(2.0);
    }

    [Fact]
    public void SelectFrozenPathPen_UntaggedPath_KeepsDefaultOrange()
    {
        var path = new FrozenWaveguidePath { Path = new RoutedPath() };

        var pen = ComponentGroupRenderer.SelectFrozenPathPen(path);

        ((SolidColorBrush)pen.Brush!).Color.ShouldBe(Color.FromArgb(200, 255, 140, 0));
    }
}

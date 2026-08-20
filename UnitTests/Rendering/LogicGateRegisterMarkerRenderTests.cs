using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using UnitTests.Integration;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Render smoke tests for the canvas register markers (issue #1112): every gate group
/// whose persisted <see cref="TruthTablePinAssignment.IsRegister"/> flag is set draws
/// a small "R" chip at its top-left corner, in the same dark-chip style as the live
/// 0/1 badges (issue #994) — so a student sees which groups hold state (the SR latch's
/// two NAND registers) and which are plain combinational (the half adder's seven
/// gates). The chip reads the persisted flag directly, so it renders on load without
/// a built network, and the Truth Table panel's Register toggle (issue #1098) shows
/// and hides it live through a canvas repaint. Renders the production renderer into a
/// <see cref="RenderTargetBitmap"/> and samples real pixels — same pattern as
/// <see cref="LogicGateSignalNameBadgeRenderTests"/>.
/// </summary>
public class LogicGateRegisterMarkerRenderTests
{
    // Marker layout mirrored from LogicGateRegisterMarkerRenderer: the bitmap origin
    // sits 10px left and 10px above the group's top-left corner, so the 16×16 chip
    // spans local x ∈ [14, 30] and local y ∈ [14, 30].
    private const int BitmapWidth = 40;
    private const int BitmapHeight = 40;
    private const int ChipLeft = 14;
    private const int ChipTop = 14;
    private const int ChipSize = 16;

    [AvaloniaFact]
    public async Task SrLatch_RendersExactlyTwoRegisterMarkers()
    {
        var canvas = await LoadExample("Logic Gate SR-Latch.lun");
        var groups = LogicGateHalfAdderExampleTests.GroupsOf(canvas);
        groups.Select(g => g.GroupName).ShouldBe(new[] { "NANDQ", "NANDQB" }, ignoreOrder: true,
            customMessage: "the SR latch ships exactly the two cross-coupled NAND registers");

        foreach (var group in groups)
        {
            var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
            using var bitmap = RenderMarkerCorner(canvas, bounds);
            var pixels = ReadPixels(bitmap);

            CountPainted(pixels).ShouldBeGreaterThan(0,
                $"group '{group.GroupName}' is a register — its marker must render");
            CountBlueText(pixels).ShouldBeGreaterThan(5,
                $"group '{group.GroupName}' paints its 'R' glyph");
        }
    }

    [AvaloniaFact]
    public async Task HalfAdder_RendersNoRegisterMarker()
    {
        var canvas = await LoadExample("Logic Gate Half Adder.lun");
        var groups = LogicGateHalfAdderExampleTests.GroupsOf(canvas);
        groups.Count.ShouldBe(7, "the half adder ships seven gates");

        foreach (var group in groups)
        {
            var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
            using var bitmap = RenderMarkerCorner(canvas, bounds);
            var pixels = ReadPixels(bitmap);

            CountPainted(pixels).ShouldBe(0,
                $"group '{group.GroupName}' is combinational — no register marker may render");
        }
    }

    [AvaloniaFact]
    public void ToggleViaTruthTablePanel_AddsAndRemovesTheMarker()
    {
        var group = TestComponentFactory.CreateComponentGroup("G");
        var child = TestComponentFactory.CreateStraightWaveGuide();
        child.PhysicalX = 0;
        child.PhysicalY = 0;
        child.WidthMicrometers = 100;
        child.HeightMicrometers = 60;
        group.AddChild(child);
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "A", "B" },
            OutputPinNames = new List<string> { "Y" },
            BiasPinNames = new List<string> { "BIAS" },
            Threshold = 0.125,
            IsRegister = false,
        };
        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(group);
        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);

        // Combinational: no marker.
        using (var bitmap = RenderMarkerCorner(canvas, bounds))
            CountPainted(ReadPixels(bitmap)).ShouldBe(0, "a combinational group draws no marker");

        // Toggle on through the panel — the repaint must fire and the marker appear.
        var component = new ComponentViewModel(group);
        canvas.Selection.SelectSingle(component);
        var repaints = 0;
        canvas.RepaintRequested = () => repaints++;
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);

        vm.IsRegister = true;
        repaints.ShouldBeGreaterThan(0, "the toggle requests a canvas repaint");
        group.TruthTablePinAssignment.IsRegister.ShouldBeTrue();
        using (var bitmap = RenderMarkerCorner(canvas, bounds))
            CountPainted(ReadPixels(bitmap)).ShouldBeGreaterThan(0, "the designation shows the marker");

        // Toggle off — the repaint fires again and the marker disappears.
        vm.IsRegister = false;
        group.TruthTablePinAssignment.IsRegister.ShouldBeFalse();
        using (var bitmap = RenderMarkerCorner(canvas, bounds))
            CountPainted(ReadPixels(bitmap)).ShouldBe(0, "clearing the designation hides the marker");
    }

    [AvaloniaFact]
    public async Task ToggleBeforeFirstExtraction_ExtractionPersistsAndRepaints_MarkerRenders()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(group);
        var component = new ComponentViewModel(group);
        canvas.Selection.SelectSingle(component);
        var repaints = 0;
        canvas.RepaintRequested = () => repaints++;
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(component, canvas);

        vm.IsRegister = true;
        group.TruthTablePinAssignment.ShouldBeNull(
            "before the first extraction there is nothing to attach the flag to — and nothing to mark");

        vm.InputPins.Single(p => p.PinName == "a").IsChecked = true;
        vm.InputPins.Single(p => p.PinName == "b").IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "y").IsChecked = true;
        vm.Threshold = 0.25;
        var repaintsBeforeExtract = repaints;
        await vm.ExtractCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeTrue("the extraction must succeed");
        group.TruthTablePinAssignment!.IsRegister.ShouldBeTrue(
            "the toggle intent rides into the assignment the extraction persists");
        repaints.ShouldBeGreaterThan(repaintsBeforeExtract,
            "persisting the designation through extraction offers the marker a repaint");

        var bounds = ComponentGroupRenderer.CalculateGroupBounds(group);
        using var bitmap = RenderMarkerCorner(canvas, bounds);
        CountPainted(ReadPixels(bitmap)).ShouldBeGreaterThan(0,
            "the pre-extraction toggle shows its marker as soon as the extraction lands");
    }

    [AvaloniaFact]
    public void UngroupedComponent_RendersNoMarker()
    {
        var canvas = new DesignCanvasViewModel();
        var lone = TestComponentFactory.CreateStraightWaveGuide();
        lone.PhysicalX = 0;
        lone.PhysicalY = 0;
        lone.WidthMicrometers = 100;
        lone.HeightMicrometers = 60;
        canvas.AddComponent(lone);

        var bounds = new Rect(lone.PhysicalX - 10, lone.PhysicalY - 10, 120, 80);
        using var bitmap = RenderMarkerCorner(canvas, bounds);
        CountPainted(ReadPixels(bitmap)).ShouldBe(0,
            "an ungrouped component never carries a register designation");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public void MarkerTooltip_IsLocalizedInEveryShippedLanguage(string languageCode)
    {
        var service = new LocalizationService();
        service.SetLanguage(languageCode);

        var text = service.Translate("LogicGate.RegisterMarker.Tooltip");

        text.ShouldNotBeNullOrWhiteSpace($"the tooltip must ship in {languageCode}");
        text.ShouldNotBe("LogicGate.RegisterMarker.Tooltip",
            $"the {languageCode} table must translate the key, not echo it");
    }

    /// <summary>Loads a shipped example through the real load path, without assembling a network.</summary>
    private static async Task<DesignCanvasViewModel> LoadExample(string fileName)
    {
        var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), fileName);
        return await LogicGateHalfAdderExampleTests.LoadCanvas(path);
    }

    /// <summary>
    /// Renders the register markers of <paramref name="canvas"/> into a bitmap whose
    /// origin sits 10px left and 10px above the group's top-left corner, so the chip
    /// lands at the local coordinates documented on the class constants.
    /// </summary>
    private static RenderTargetBitmap RenderMarkerCorner(DesignCanvasViewModel canvas, Rect groupBounds)
    {
        var origin = new Point(groupBounds.Left - 10, groupBounds.Top - 10);
        var rc = new CanvasRenderContext
        {
            ViewModel = canvas,
            InteractionState = new CanvasInteractionState(),
            Zoom = 1.0,
            Bounds = new Rect(origin, new Size(BitmapWidth, BitmapHeight)),
        };
        var bitmap = new RenderTargetBitmap(new PixelSize(BitmapWidth, BitmapHeight));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, BitmapWidth, BitmapHeight));
            using (ctx.PushTransform(Matrix.CreateTranslation(-origin.X, -origin.Y)))
            {
                LogicGateRegisterMarkerRenderer.Render(ctx, rc);
            }
        }
        return bitmap;
    }

    /// <summary>Counts every non-black pixel inside the chip area.</summary>
    private static int CountPainted(byte[] pixels)
    {
        var count = 0;
        for (var y = ChipTop; y < ChipTop + ChipSize; y++)
        {
            for (var x = ChipLeft; x < ChipLeft + ChipSize; x++)
            {
                var i = (y * BitmapWidth + x) * 4;
                if (pixels[i] > 15 || pixels[i + 1] > 15 || pixels[i + 2] > 15)
                    count++;
            }
        }
        return count;
    }

    /// <summary>Counts pixels of the muted-blue 'R' glyph: blue channel clearly dominates.</summary>
    private static int CountBlueText(byte[] pixels)
    {
        var count = 0;
        for (var y = ChipTop; y < ChipTop + ChipSize; y++)
        {
            for (var x = ChipLeft; x < ChipLeft + ChipSize; x++)
            {
                var i = (y * BitmapWidth + x) * 4;
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                if (b > 200 && b > r + 40)
                    count++;
            }
        }
        return count;
    }

    private static byte[] ReadPixels(RenderTargetBitmap bitmap)
    {
        var stride = BitmapWidth * 4;
        var buffer = Marshal.AllocHGlobal(stride * BitmapHeight);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, BitmapWidth, BitmapHeight), buffer, stride * BitmapHeight, stride);
            var bytes = new byte[stride * BitmapHeight];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

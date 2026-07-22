using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CAP.Avalonia.Controls;
using CAP.Avalonia.Controls.Rendering;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for issue #724: renders one pin of each (MatterType × Polarization)
/// glyph side by side — electrical pad, optical TE, optical TM, optical Both — via the real
/// <see cref="PinRenderer"/> drawing path. Confirms visually that the electrical pad (formerly
/// rendering round, like the field-reported Probe Pad) and the optical TM pin (formerly a plain
/// square, like the field-reported "Adiabatic Coupler TM 1550") are now clearly distinct glyphs.
/// Writes a PNG + manifest.json to <c>artifacts/ui-screenshots/issue-724/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class Issue724PinGlyphScreenshotTests
{
    private const double Scale = 4.0;

    [AvaloniaFact]
    public void CapturePinGlyphShowcase()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return; // opt-in: heavy headless render, only on explicit request (see UiScreenshotTests)
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var components = BuildShowcaseComponents();
        var control = new PinGlyphShowcaseControl { Components = components };
        var window = new Window { Width = 650, Height = 220, Content = control, Background = Brushes.Black };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull("render miss for pin glyph showcase");
        var path = Path.Combine(outputDir, "pin-glyphs.png");
        byte[] bytes;
        using (bitmap)
            bytes = ScreenshotArtifacts.SavePng(bitmap!, path);
        bytes.Length.ShouldBeGreaterThan(0);

        WriteManifest(outputDir);
    }

    /// <summary>One pin per glyph, spaced along the X axis at a shared Y so all four line up.</summary>
    private static List<(string Label, ComponentViewModel Vm)> BuildShowcaseComponents() => new()
    {
        BuildShowcasePin("Electrical Pad", 20, 30, MatterType.Electricity, PolarizationKind.TE),
        BuildShowcasePin("Optical TE", 55, 30, MatterType.Light, PolarizationKind.TE),
        BuildShowcasePin("Optical TM", 90, 30, MatterType.Light, PolarizationKind.TM),
        BuildShowcasePin("Optical Both", 125, 30, MatterType.Light, PolarizationKind.Both),
    };

    /// <summary>
    /// Builds a minimal component with a single physical pin at (x, y) carrying the given
    /// MatterType/Polarization, wrapped in a <see cref="ComponentViewModel"/> for rendering.
    /// </summary>
    private static (string Label, ComponentViewModel Vm) BuildShowcasePin(
        string label, double x, double y, MatterType matterType, PolarizationKind polarization)
    {
        var sMatrix = new SMatrix(new List<Guid>(), new List<(Guid sliderID, double value)>());
        var logicalPin = new Pin("p0", 0, matterType, RectSide.Right) { Polarization = polarization };
        var physicalPin = new PhysicalPin { Name = "p0", LogicalPin = logicalPin };

        var component = new Component(
            new Dictionary<int, SMatrix> { { 1550, sMatrix } },
            new List<CAP_Core.Components.Core.Slider>(),
            "test", label,
            new Part[1, 1] { { new Part() } },
            -1, $"showcase_{Guid.NewGuid():N}", new DiscreteRotation(),
            new List<PhysicalPin> { physicalPin })
        {
            PhysicalX = x,
            PhysicalY = y,
            WidthMicrometers = 10,
            HeightMicrometers = 10,
        };
        physicalPin.ParentComponent = component;

        return (label, new ComponentViewModel(component));
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "pin-glyphs.png", "caption": "Issue #724: electrical pad (golden filled square + contact rim), optical TE (circle), optical TM (diamond — distinct from the electrical pad), optical Both (circle + diamond outline). The electrical pad no longer renders round, and the optical TM pin no longer looks electrical."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-724</c> (or <c>UI_SHOT_DIR/issue-724</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-724");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-724");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-724");
    }

    /// <summary>
    /// Draws each showcase component's pin (scaled 4x for visibility) via the real
    /// <see cref="PinRenderer"/> path, plus a plain-scale label under each.
    /// </summary>
    private sealed class PinGlyphShowcaseControl : Control
    {
        public required List<(string Label, ComponentViewModel Vm)> Components { get; init; }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            var rc = new CanvasRenderContext
            {
                ViewModel = new DesignCanvasViewModel(),
                InteractionState = new CanvasInteractionState(),
            };
            var renderer = new PinRenderer();

            using (context.PushTransform(Matrix.CreateScale(Scale, Scale)))
            {
                foreach (var (_, vm) in Components)
                    renderer.DrawComponentPins(context, vm, rc);
            }

            var typeface = new Typeface("Arial");
            foreach (var (label, vm) in Components)
            {
                var (px, py) = vm.Component.PhysicalPins[0].GetAbsolutePosition();
                var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 13, Brushes.White);
                context.DrawText(text, new Point(px * Scale - 55, py * Scale + 25));
            }
        }
    }
}

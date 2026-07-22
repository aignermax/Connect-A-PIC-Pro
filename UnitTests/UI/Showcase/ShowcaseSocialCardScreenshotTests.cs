using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase: the 1200x630 Open-Graph/Twitter social card behind the landing
/// page's link preview. Full-bleed background: a live-simulation crop of the staged hero
/// chip (power-flow colored waveguides plus the golden DC metal traces), overlaid with a
/// navy legibility gradient and the landing page's typography — LUNIMA wordmark, green
/// eyebrow, and the "Draw the chip. / Watch the light." claim, sized to stay readable at
/// thumbnail width. Opt-in via <c>UI_SHOT_DIR</c>; the PNG lands in <c>UI_SHOT_DIR/v0.12/</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseSocialCardScreenshotTests
{
    private const int CardWidth = 1200;
    private const int CardHeight = 630;

    /// <summary>World-space (µm) region of the staged chip behind the card: the MZI arms
    /// with the electro-optic phase shifter, probe/bond pads with golden metal traces and
    /// the 2x2 combiner — aspect close to the card's 1.9:1 so UniformToFill barely crops.</summary>
    private static readonly (double X, double Y, double W, double H) Region = (330, 0, 1260, 650);

    private static readonly Color Navy = Color.Parse("#0A1020");

    [AvaloniaFact]
    public async Task CaptureSocialCard()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            using var chipCrop = await CaptureSimulatedChipCropAsync();

            var card = new Window
            {
                Width = CardWidth, Height = CardHeight,
                Background = new SolidColorBrush(Navy),
                Content = BuildCardContent(chipCrop),
            };
            card.Show();
            Dispatcher.UIThread.RunJobs();
            ShowcaseCapture.CaptureWindow(
                card, Path.Combine(ShowcaseCapture.OutputDirectory(), "social-card.png"));
            card.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>Runs the real CW simulation on the staged chip (power-flow overlay on) and
    /// returns the canvas crop of <see cref="Region"/> as the card's background plate.</summary>
    private static async Task<RenderTargetBitmap> CaptureSimulatedChipCropAsync()
    {
        var (vm, window, _) = await ShowcaseCircuit.BootStagedMainWindowAsync();
        await vm.RunSimulationCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.Canvas.ShowPowerFlow.ShouldBeTrue("the CW run must enable the power-flow overlay");
        ShowcaseCircuit.SetView(window, vm, Region);
        ShowcaseCapture.PumpRenderLoop();

        var canvasControl = window.GetVisualDescendants()
            .OfType<CAP.Avalonia.Controls.DesignCanvas>().First();
        double zoom = canvasControl.Zoom;
        var canvasBounds = ShowcaseCapture.BoundsIn(window, canvasControl);
        var crop = new PixelRect(
            canvasBounds.X + (int)(Region.X * zoom + vm.Canvas.PanX),
            canvasBounds.Y + (int)(Region.Y * zoom + vm.Canvas.PanY),
            (int)(Region.W * zoom), (int)(Region.H * zoom))
            .Intersect(canvasBounds);

        using var frame = ShowcaseCapture.CaptureFrame(window, "social-card (chip)");
        window.Close();
        Dispatcher.UIThread.RunJobs();

        // Copy the crop into its own bitmap so the full frame can be disposed.
        var plate = new RenderTargetBitmap(new PixelSize(crop.Width, crop.Height));
        using (var ctx = plate.CreateDrawingContext())
        {
            ctx.DrawImage(frame,
                new Rect(crop.X, crop.Y, crop.Width, crop.Height),
                new Rect(0, 0, crop.Width, crop.Height));
        }
        return plate;
    }

    /// <summary>Full-bleed chip plate, navy legibility gradients, and the text block.</summary>
    private static Control BuildCardContent(RenderTargetBitmap chipCrop)
    {
        var root = new Panel();
        root.Children.Add(new Image { Source = chipCrop, Stretch = Stretch.UniformToFill });
        root.Children.Add(new Border { Background = HorizontalFade() });
        root.Children.Add(new Border { Background = BottomFade() });
        root.Children.Add(BuildTextBlock());
        return root;
    }

    /// <summary>Left-anchored text: wordmark, eyebrow, claim, platform line — the landing
    /// page's hero typography (mono eyebrow/wordmark, extra-bold claim, amber accent).</summary>
    private static Control BuildTextBlock()
    {
        var mono = new FontFamily("Consolas,Menlo,monospace");
        var sans = new FontFamily("Segoe UI,Inter,Helvetica,Arial,sans-serif");
        var stack = new StackPanel
        {
            Margin = new Thickness(66, 0, 380, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        stack.Children.Add(new TextBlock
        {
            Text = "LUNIMA",
            FontFamily = mono, FontSize = 27, FontWeight = FontWeight.SemiBold,
            LetterSpacing = 9, Foreground = new SolidColorBrush(Color.Parse("#E9EEF7")),
            Margin = new Thickness(0, 0, 0, 34),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "OPEN-SOURCE PHOTONIC IC DESIGN",
            FontFamily = mono, FontSize = 17.5, LetterSpacing = 2.6,
            Foreground = new SolidColorBrush(Color.Parse("#43DE85")),
            Margin = new Thickness(0, 0, 0, 18),
        });
        stack.Children.Add(Claim("Draw the chip.", Color.Parse("#E9EEF7"), sans));
        stack.Children.Add(Claim("Watch the light.", Color.Parse("#F2A83B"), sans));
        stack.Children.Add(new TextBlock
        {
            Text = "Windows · Linux · macOS — MIT licensed",
            FontFamily = mono, FontSize = 18, LetterSpacing = 1.2,
            Foreground = new SolidColorBrush(Color.Parse("#8FA0BC")),
            Margin = new Thickness(0, 26, 0, 0),
        });
        return stack;
    }

    private static TextBlock Claim(string text, Color color, FontFamily sans) => new()
    {
        Text = text,
        FontFamily = sans, FontSize = 76, FontWeight = FontWeight.ExtraBold,
        LetterSpacing = -1.6, LineHeight = 94,
        Foreground = new SolidColorBrush(color),
    };

    /// <summary>Navy fade from the text side into the chip (opaque → transparent).</summary>
    private static LinearGradientBrush HorizontalFade() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(246, Navy.R, Navy.G, Navy.B), 0),
            new GradientStop(Color.FromArgb(216, Navy.R, Navy.G, Navy.B), 0.44),
            new GradientStop(Color.FromArgb(30, Navy.R, Navy.G, Navy.B), 0.80),
            new GradientStop(Color.FromArgb(0, Navy.R, Navy.G, Navy.B), 1),
        },
    };

    /// <summary>Soft bottom vignette so canvas HUD remnants never fight the claim.</summary>
    private static LinearGradientBrush BottomFade() => new()
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0.55, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(150, Navy.R, Navy.G, Navy.B), 0),
            new GradientStop(Color.FromArgb(0, Navy.R, Navy.G, Navy.B), 1),
        },
    };
}

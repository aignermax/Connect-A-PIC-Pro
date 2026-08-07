using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP.Avalonia.Views.Panels;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Opt-in screenshot walkthrough for the parametric parameter editor:
/// renders the properties panel with an MMI (two labeled parameter rows), an
/// edited MMI, and a Directional Coupler selected. Runs only when the env var
/// <c>UI_SHOT_DIR</c> points at the output directory.
/// </summary>
[Trait("Category", "UiScreenshots")]
[Collection("LocalizationSingleton")]
public class ParametricEditorScreenshotTests
{
    private const int MinDistinctSampledColors = 10;
    private const int PanelWidth = 450;
    private const int PanelHeight = 620;

    [AvaloniaFact]
    public void CaptureParametricEditorWalkthrough()
    {
        var outputDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (string.IsNullOrEmpty(outputDir)) return;
        Directory.CreateDirectory(outputDir);

        var vm = MainViewModelTestHelper.CreateMainViewModel();
        var templates = TestPdkLoader.LoadFromPdk("demo-pdk.json");

        // 01: MMI selected at its documented defaults (0.3 dB / 50 %).
        var mmi = SelectFresh(vm, templates.First(t => t.Name == "1x2 MMI Splitter"));
        Capture(vm, outputDir, "01-initial.png");
        vm.RightPanel.SelectedComponentEditor
            .ShouldBeOfType<ParametricParametersEditorViewModel>();

        // 02: parameters edited per instance — sliders and numeric fields move.
        var editor = (ParametricParametersEditorViewModel)vm.RightPanel.SelectedComponentEditor!;
        editor.Rows[0].Value = 1.5;
        editor.Rows[1].Value = 80;
        Capture(vm, outputDir, "02-mmi-edited.png");
        mmi.Component.GetSlider(1)!.Value.ShouldBe(80, 1e-9);

        // 03: Directional Coupler with its single coupling-ratio parameter.
        SelectFresh(vm, templates.First(t => t.Name == "Directional Coupler"));
        Capture(vm, outputDir, "03-directional-coupler.png");
        vm.RightPanel.SelectedComponentEditor
            .ShouldBeOfType<ParametricParametersEditorViewModel>();
    }

    /// <summary>Places a fresh instance of the template and selects it on the canvas.</summary>
    private static CAP.Avalonia.ViewModels.Canvas.ComponentViewModel SelectFresh(
        MainViewModel vm, ComponentTemplate template)
    {
        var component = ComponentTemplates.CreateFromTemplate(template, 100, 100);
        var componentVm = vm.Canvas.AddComponent(component, template.Name, template.PdkSource);
        vm.Canvas.SelectedComponent = componentVm;
        Dispatcher.UIThread.RunJobs();
        return componentVm;
    }

    /// <summary>Renders the properties panel offscreen and saves it as a PNG.</summary>
    private static void Capture(MainViewModel vm, string outputDir, string filename)
    {
        var window = new Window
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Content = new SelectedComponentPropertiesPanel { DataContext = vm }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        if (bitmap == null)
        {
            // A pending layout/render pass can make the first capture miss;
            // pump the dispatcher once more and retry before failing.
            Dispatcher.UIThread.RunJobs();
            bitmap = window.CaptureRenderedFrame();
        }
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"render must produce a frame for {filename}");
        var path = Path.Combine(outputDir, filename);
        using (bitmap)
        {
            CountDistinctSampledColors(bitmap).ShouldBeGreaterThan(MinDistinctSampledColors,
                $"near-blank render for {filename}");
            ScreenshotArtifacts.SavePng(bitmap, path);
        }
        Console.WriteLine($"[OK] {path}");
    }

    private const int SampleGridSize = 64;

    /// <summary>Counts distinct sampled ARGB values to detect blank renders.</summary>
    private static int CountDistinctSampledColors(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        int width = fb.Size.Width;
        int height = fb.Size.Height;
        if (width <= 0 || height <= 0) return 0;

        int stepX = Math.Max(1, width / SampleGridSize);
        int stepY = Math.Max(1, height / SampleGridSize);
        var colors = new HashSet<int>();
        for (int y = 0; y < height; y += stepY)
        {
            var rowAddr = fb.Address + y * fb.RowBytes;
            for (int x = 0; x < width; x += stepX)
                colors.Add(Marshal.ReadInt32(rowAddr, x * 4));
        }
        return colors.Count;
    }
}

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Views;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Showcase;

/// <summary>
/// v0.12 feature-showcase (window motifs): the unified component editor in edit mode with a
/// rendered geometry preview, the fabrication-process editor, and the two-language composite
/// of the staged MainWindow. Opt-in via <c>UI_SHOT_DIR</c>; PNGs land in <c>UI_SHOT_DIR/v0.12/</c>.
/// </summary>
[Trait("Category", "Showcase")]
[Collection("LocalizationSingleton")]
public class ShowcaseWindowScreenshotTests
{
    private const string EditorCode =
        "import gdsfactory as gf\n\n" +
        "# 1x2 multimode interference splitter on the strip cross-section\n" +
        "component = gf.components.mmi1x2(\n" +
        "    width_mmi=6.0,\n    length_mmi=20.0,\n    width_taper=1.4,\n" +
        "    cross_section=\"strip\",\n)";

    /// <summary>The unified editor in edit mode with code + rendered geometry preview.</summary>
    [AvaloniaFact]
    public async Task CaptureComponentEditor()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var vm = BuildEditorViewModel();
            await vm.RunPreviewCommand.ExecuteAsync(null);
            vm.HasPreview.ShouldBeTrue("the stubbed geometry render must populate the preview");

            var window = new NewComponentWindow { Width = 560, Height = 880, DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ShowcaseCapture.CaptureWindow(
                window, Path.Combine(ShowcaseCapture.OutputDirectory(), "component-editor.png"));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>The fabrication-process editor with a Cornerstone-like SiN layer stack.</summary>
    [AvaloniaFact]
    public async Task CaptureProcessEditor()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(() =>
        {
            var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
            var process = ShowcaseProcess.CreateSinProcess();
            vm.Load(process);
            vm.StatusText = string.Format(
                LocalizationService.Instance.Translate("ProcessMgmt.Status.EditingProcess"),
                process.Name);

            var window = new ProcessManagementWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ShowcaseCapture.CaptureWindow(
                window, Path.Combine(ShowcaseCapture.OutputDirectory(), "process-editor.png"));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });
    }

    /// <summary>English and Japanese MainWindow (same staged chip) side by side.</summary>
    [AvaloniaFact]
    public async Task CaptureMultilanguageComposite()
    {
        if (!ShowcaseCapture.Enabled) return;
        await ShowcaseCapture.WithEnglishUiAsync(async () =>
        {
            var (_, window, _) = await ShowcaseCircuit.BootStagedMainWindowAsync();

            using var english = ShowcaseCapture.CaptureFrame(window, "multilanguage (en)");
            LocalizationService.Instance.SetLanguage("ja");
            Dispatcher.UIThread.RunJobs();
            using var japanese = ShowcaseCapture.CaptureFrame(window, "multilanguage (ja)");
            window.Close();
            Dispatcher.UIThread.RunJobs();

            // The same left half (toolbar, panels, canvas) in both languages, one PNG.
            var crop = new global::Avalonia.PixelRect(0, 0, 738, 620);
            ShowcaseCapture.ComposeSideBySide(
                Path.Combine(ShowcaseCapture.OutputDirectory(), "multilanguage.png"),
                new[] { ((global::Avalonia.Media.Imaging.Bitmap)english, crop), (japanese, crop) });
        });
    }

    /// <summary>
    /// Builds the edit-mode editor VM the way the ✏ flow does: the component exists in a
    /// (temp) user PDK, so <c>LoadForEdit</c> finds its PDK among the custom-PDK choices.
    /// </summary>
    private static NewComponentViewModel BuildEditorViewModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lunima-showcase-editor-{Guid.NewGuid():N}");
        var store = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "Demo SOI 220nm" };
        var pdkPath = store.CreateNamedPdkWithProcess(ShowcaseCircuit.PdkName, process, "gdsfactory", null);
        store.AppendToExistingPdk(pdkPath, new PdkComponentDraft
        {
            Name = "1x2 MMI Splitter",
            WidthMicrometers = 32,
            HeightMicrometers = 6.4,
            RawCode = EditorCode,
            RawCodeBackend = "gdsfactory",
            Pins = new()
            {
                new PhysicalPinDraft { Name = "o1" },
                new PhysicalPinDraft { Name = "o2" },
                new PhysicalPinDraft { Name = "o3" },
            },
        });

        var extractor = new ComponentGeometryExtractor(
            ShowcaseProcess.MmiPreviewRenderer(), ShowcaseProcess.MmiPreviewRenderer());
        var vm = new NewComponentViewModel(extractor, Mock.Of<IFdtdSMatrixService>(), store,
            new List<ProcessDefinition> { process });
        vm.LoadForEdit(new ComponentTemplate
        {
            Name = "1x2 MMI Splitter",
            RawCode = EditorCode,
            RawCodeBackend = "gdsfactory",
            PdkSource = ShowcaseCircuit.PdkName,
        }).ShouldBeTrue("the editor must resolve the temp user PDK");
        vm.IsEditMode.ShouldBeTrue();
        return vm;
    }
}

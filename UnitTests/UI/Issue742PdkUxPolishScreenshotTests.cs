using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services.AddCustomComponent;
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
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual walkthrough for PR #742 (PDK/editor UX polish): renders the compact PDK Mgmt
/// header with its enable/disable-all icon actions, the unified component editor in edit
/// mode (opened by canvas right-click → "Edit Component…"), and the per-component trash
/// restore rows. Writes PNGs + manifest.json to <c>artifacts/ui-screenshots/issue-739/</c>.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class Issue742PdkUxPolishScreenshotTests
{
    [AvaloniaFact]
    public void CapturePdkUxPolishWalkthrough()
    {
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        CaptureMainWindowPdkHeader(Path.Combine(outputDir, "01-pdk-mgmt-header.png"));
        CaptureEditComponentWindow(Path.Combine(outputDir, "02-edit-component-window.png"));
        CaptureTrashPerComponent(Path.Combine(outputDir, "03-trash-per-component.png"));

        WriteManifest(outputDir);
    }

    /// <summary>
    /// 01 — the real MainWindow with a seeded PDK list: the "PDK Mgmt" header now carries
    /// compact ☑/☐ (enable/disable all) icon actions and the old bottom button row is gone.
    /// DataContext is assigned only after Show() so the production Loaded wiring (which
    /// needs App.Services) no-ops in the headless host.
    /// </summary>
    private static void CaptureMainWindowPdkHeader(string path)
    {
        var vm = MainViewModelTestHelper.CreateMainViewModel();
        vm.LeftPanel.PdkManager.RegisterPdk("Demo SOI 220nm", null, isBundled: true, componentCount: 12);
        vm.LeftPanel.PdkManager.RegisterPdk("My SiN Library", "/tmp/my-sin-library.json", isBundled: false, componentCount: 4);

        var window = new MainWindow { Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs(); // Loaded fires here with a null DataContext → no DI wiring
        window.DataContext = vm;
        Dispatcher.UIThread.RunJobs();

        CaptureWindow(window, path);
    }

    /// <summary>
    /// 02 — the unified component editor (NewComponentWindow) in edit mode: taller window,
    /// always-visible scrollbar, edit banner, and a title carrying the component's name.
    /// This is the window the renamed canvas right-click entry "Edit Component…" opens.
    /// </summary>
    private void CaptureEditComponentWindow(string path)
    {
        var store = new UserPdkStore(_editRoot, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var pdkPath = store.CreateNamedPdkWithProcess("My SiN Library", process, "gdsfactory", null);
        store.AppendToExistingPdk(pdkPath, new PdkComponentDraft
        {
            Name = "Ring Resonator", WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = rawCode, RawCodeBackend = "gdsfactory",
            Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
        });

        var extractor = new ComponentGeometryExtractor(
            Mock.Of<IComponentPreviewRenderer>(), Mock.Of<IComponentPreviewRenderer>());
        var vm = new NewComponentViewModel(extractor, Mock.Of<IFdtdSMatrixService>(), store,
            new List<ProcessDefinition> { process });
        vm.LoadForEdit(new ComponentTemplate
        {
            Name = "Ring Resonator", RawCode = rawCode, RawCodeBackend = "gdsfactory",
            PdkSource = "My SiN Library",
        });
        vm.IsEditMode.ShouldBeTrue();
        vm.WindowTitle.ShouldBe("Edit Component: Ring Resonator");

        var window = new NewComponentWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        CaptureWindow(window, path);
    }

    /// <summary>
    /// 03 — the trash flyout: a removed component is now listed under its own name with the
    /// owning PDK as detail, so Restore brings back exactly that component (not the whole
    /// backup diff), alongside a deleted-PDK entry that still restores the full PDK.
    /// </summary>
    private void CaptureTrashPerComponent(string path)
    {
        var store = new UserPdkStore(_trashRoot, new PdkJsonSaver(), new PdkLoader());
        PdkComponentDraft Comp(string n) => new()
        {
            Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
            Pins = new() { new() { Name = "o1" }, new() { Name = "o2" } },
        };
        var process = new ProcessDefinition { Name = "Demo SOI 220nm" };

        // A whole deleted PDK (restores as one unit) …
        var libA = store.SaveToNamedPdk("My SiN Library", process, Comp("Ring Resonator"), "gdsfactory", null);
        store.MoveToTrash(libA);
        // … and two individually removed components from a living PDK (per-element rows).
        var libB = store.SaveToNamedPdk("Prototype Kit", process, Comp("Test MMI"), "gdsfactory", null);
        store.SaveToNamedPdk("Prototype Kit", process, Comp("Spiral Delay"), "gdsfactory", null);
        store.RemoveComponent(libB, "Test MMI");

        var vm = new CAP.Avalonia.ViewModels.Panels.PdkTrash.PdkTrashViewModel(store.CreateTrashService());
        vm.Refresh();

        var window = new Avalonia.Controls.Window
        {
            Width = 380, Height = 420,
            Content = new CAP.Avalonia.Views.Panels.PdkTrashPanel { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        CaptureWindow(window, path);
    }

    private static void CaptureWindow(Avalonia.Controls.Window window, string path)
    {
        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull($"render miss for {Path.GetFileName(path)}");
        byte[] bytes;
        using (bitmap)
            bytes = ScreenshotArtifacts.SavePng(bitmap!, path);
        bytes.Length.ShouldBeGreaterThan(0);
    }

    private static void WriteManifest(string outputDir)
    {
        const string manifest = """
        [
          {"file": "01-pdk-mgmt-header.png", "caption": "The PDK Mgmt header now packs enable-all (☑) / disable-all (☐) icon actions next to trash and +, replacing the old bottom button row and process label, while app-wide buttons and combo boxes render flatter."},
          {"file": "02-edit-component-window.png", "caption": "The unified component editor — now also opened by canvas right-click 'Edit Component…' — is taller (800px), keeps its scrollbar always visible, shows the edit banner, and titles itself 'Edit Component: Ring Resonator'."},
          {"file": "03-trash-per-component.png", "caption": "Trash lists each removed component by its own name (PDK shown as detail), so Restore recovers exactly the clicked component instead of the whole backup diff."}
        ]
        """;
        ScreenshotArtifacts.WriteText(Path.Combine(outputDir, "manifest.json"), manifest);
    }

    /// <summary>Repo-root <c>artifacts/ui-screenshots/issue-739</c> (or <c>UI_SHOT_DIR/issue-739</c>).</summary>
    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "issue-739");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "issue-739");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "issue-739");
    }

    private readonly string _editRoot =
        Path.Combine(Path.GetTempPath(), $"lunima-742-shot-edit-{Guid.NewGuid():N}");
    private readonly string _trashRoot =
        Path.Combine(Path.GetTempPath(), $"lunima-742-shot-trash-{Guid.NewGuid():N}");
}

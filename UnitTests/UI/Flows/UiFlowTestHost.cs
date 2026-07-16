using Avalonia.Threading;
using CAP.Avalonia;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Views;
using CAP_Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using UnitTests.Helpers;

namespace UnitTests.UI.Flows;

/// <summary>
/// Boots the REAL <see cref="MainWindow"/> (code-behind wireups included) headless for
/// click-through user-story tests: real views, real bindings, simulated mouse/keyboard.
/// Only the outermost seams are faked — geometry rendering (no Python in tests), the
/// user-pdks root (temp dir instead of the developer's real folder), preferences (temp file),
/// and <c>App.Services</c> (a minimal provider carrying just the temp <see cref="UserPdkStore"/>).
/// </summary>
internal sealed class UiFlowTestHost : IDisposable
{
    public MainWindow Window { get; }

    public MainViewModel Vm { get; }

    /// <summary>Store rooted in <see cref="UserPdkRoot"/> — where forks and new PDKs land.</summary>
    public UserPdkStore UserPdkStore { get; }

    public string UserPdkRoot { get; }

    private readonly string _prefsPath;

    public UiFlowTestHost()
    {
        var id = Guid.NewGuid().ToString("N");
        UserPdkRoot = Path.Combine(Path.GetTempPath(), $"lunima-uiflow-pdks-{id}");
        _prefsPath = Path.Combine(Path.GetTempPath(), $"lunima-uiflow-prefs-{id}.json");

        var prefs = new UserPreferencesService(_prefsPath);
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var libraryManager = new GroupLibraryManager();
        UserPdkStore = new UserPdkStore(UserPdkRoot, new PdkJsonSaver(), new PdkLoader());

        var leftPanel = new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), prefs,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            errorConsole: new ErrorConsoleService(),
            addCustomComponentDeps: new AddCustomComponentDependencies(
                new ComponentGeometryExtractor(StubRenderer(), StubRenderer()),
                Fdtd: null, UserPdkStore))
        {
            UserPdkStartupRootOverride = UserPdkRoot,
        };

        Vm = MainViewModelTestHelper.CreateMainViewModel(
            commandManager: commandManager,
            preferencesService: prefs,
            libraryManager: libraryManager,
            canvas: canvas,
            leftPanel: leftPanel);

        // The startup process picker (issue #570) would otherwise open modally over the window
        // right after Loaded; a pre-established Playground process makes it a no-op.
        Vm.FileOperations.SetActiveProcess(ActiveProcessSelection.Playground(), markDirty: false);

        // MainWindow's code-behind resolves optional collaborators via App.Services (all
        // null-tolerant except the flows under test, which need the UserPdkStore).
        var services = new ServiceCollection();
        services.AddSingleton(UserPdkStore);
        App.OverrideServicesForTesting(services.BuildServiceProvider());

        Window = new MainWindow { DataContext = Vm };
        Window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The library ListBox bound to <c>LeftPanel.FilteredTemplates</c>.</summary>
    public global::Avalonia.Controls.ListBox LibraryListBox =>
        UiInput.Descendants<global::Avalonia.Controls.ListBox>(Window)
            .First(l => ReferenceEquals(l.ItemsSource, Vm.LeftPanel.FilteredTemplates));

    /// <summary>
    /// Geometry renderer stub: every render succeeds with a small 2-pin cell, so "Save changes"
    /// (which re-renders the edited code) works without a Python environment.
    /// </summary>
    private static IComponentPreviewRenderer StubRenderer()
    {
        var ok = new NazcaPreviewResult
        {
            Success = true,
            XMin = 0,
            YMin = 0,
            XMax = 8,
            YMax = 1.5,
            Pins = new List<NazcaPreviewPin>
            {
                new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
                new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 },
            },
        };
        var mock = new Mock<IComponentPreviewRenderer>();
        mock.Setup(r => r.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ok);
        mock.Setup(r => r.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ok);
        return mock.Object;
    }

    public void Dispose()
    {
        foreach (var owned in Window.OwnedWindows.ToList())
            owned.Close();
        Window.Close();
        Dispatcher.UIThread.RunJobs();

        try { File.Delete(_prefsPath); } catch (IOException) { }
        if (Directory.Exists(UserPdkRoot))
        {
            try { Directory.Delete(UserPdkRoot, true); } catch (IOException) { }
        }
    }
}

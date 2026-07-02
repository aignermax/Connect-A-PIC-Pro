using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Settings;
using Shouldly;
using Xunit;

namespace UnitTests.Settings;

/// <summary>
/// Unit tests for the settings registry pattern.
/// Verifies that <see cref="SettingsWindowViewModel"/> correctly enumerates pages
/// and that individual page implementations meet the contract.
/// </summary>
public class SettingsRegistryTests
{
    // -----------------------------------------------------------------------
    // SettingsWindowViewModel — registry contract
    // -----------------------------------------------------------------------

    [Fact]
    public void SettingsWindowViewModel_SelectsFirstPageByDefault()
    {
        // Arrange
        var pages = new List<ISettingsPage>
        {
            new StubSettingsPage("Alpha"),
            new StubSettingsPage("Beta"),
        };

        // Act
        var vm = new SettingsWindowViewModel(pages);

        // Assert
        vm.SelectedPage.ShouldNotBeNull();
        vm.SelectedPage!.Title.ShouldBe("Alpha");
    }

    [Fact]
    public void SettingsWindowViewModel_ExposesAllRegisteredPages()
    {
        // Arrange
        var pages = new List<ISettingsPage>
        {
            new StubSettingsPage("A"),
            new StubSettingsPage("B"),
            new StubSettingsPage("C"),
        };

        // Act
        var vm = new SettingsWindowViewModel(pages);

        // Assert
        vm.Pages.Count.ShouldBe(3);
    }

    [Fact]
    public void SettingsWindowViewModel_WithNoPages_HasNullSelectedPage()
    {
        var vm = new SettingsWindowViewModel(Enumerable.Empty<ISettingsPage>());
        vm.SelectedPage.ShouldBeNull();
    }

    [Fact]
    public void SettingsWindowViewModel_ChangingSelectedPage_UpdatesProperty()
    {
        // Arrange
        var page1 = new StubSettingsPage("Page1");
        var page2 = new StubSettingsPage("Page2");
        var vm = new SettingsWindowViewModel(new[] { page1, page2 });

        // Act
        vm.SelectedPage = page2;

        // Assert
        vm.SelectedPage!.Title.ShouldBe("Page2");
    }

    // -----------------------------------------------------------------------
    // GridSnapSettingsPage
    // -----------------------------------------------------------------------

    [Fact]
    public void GridSnapSettingsPage_ContractIsSatisfied()
    {
        var canvas = new DesignCanvasViewModel();
        ISettingsPage page = new GridSnapSettingsPage(canvas);

        page.Title.ShouldNotBeNullOrEmpty();
        page.Icon.ShouldNotBeNullOrEmpty();
        page.ViewModel.ShouldNotBeNull();
        page.ViewModel.ShouldBeOfType<GridSnapSettingsViewModel>();
    }

    [Fact]
    public void GridSnapSettingsPage_ViewModelWrapsCanvasGridSnap()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.GridSnap.IsEnabled = true;
        canvas.GridSnap.GridSizeMicrometers = 25.0;

        var page = new GridSnapSettingsPage(canvas);
        var vm = (GridSnapSettingsViewModel)page.ViewModel;

        vm.GridSnap.IsEnabled.ShouldBeTrue();
        vm.GridSnap.GridSizeMicrometers.ShouldBe(25.0);
    }

    [Fact]
    public void GridSnapSettingsPage_ViewModelChange_AffectsCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var page = new GridSnapSettingsPage(canvas);
        var vm = (GridSnapSettingsViewModel)page.ViewModel;

        // Act: change via settings ViewModel
        vm.GridSnap.IsEnabled = true;
        vm.GridSnap.GridSizeMicrometers = 100.0;

        // Assert: same objects — canvas reflects the change immediately
        canvas.GridSnap.IsEnabled.ShouldBeTrue();
        canvas.GridSnap.GridSizeMicrometers.ShouldBe(100.0);
    }

    [Fact]
    public void GridSnapSettingsPage_AlignmentGuideChange_AffectsCanvas()
    {
        // Mirrors the GridSnap test for the second wrapped property — a
        // regression in the AlignmentGuide wiring (e.g. a local copy instead
        // of the canvas reference) would otherwise slip through.
        var canvas = new DesignCanvasViewModel();
        var page = new GridSnapSettingsPage(canvas);
        var vm = (GridSnapSettingsViewModel)page.ViewModel;

        vm.AlignmentGuide.IsEnabled = true;

        canvas.AlignmentGuide.IsEnabled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // Page contract — title, icon, category stability
    // -----------------------------------------------------------------------

    [Fact]
    public void GeneralSettingsPage_StableContract()
    {
        ISettingsPage page = new GeneralSettingsPage();

        page.Title.ShouldBe("General");
        page.Icon.ShouldBe("⚙");
        page.Category.ShouldBeNull();
        page.ViewModel.ShouldBeOfType<GeneralSettingsViewModel>();
    }

    [Fact]
    public void GridSnapSettingsPage_StableContract()
    {
        // Guards the display string — a refactor silently turning
        // "Grid & Alignment" into "Grid" would otherwise ship.
        ISettingsPage page = new GridSnapSettingsPage(new DesignCanvasViewModel());

        page.Title.ShouldBe("Grid & Alignment");
        page.Icon.ShouldBe("⊞");
        page.Category.ShouldBe("Canvas");
    }

    [Fact]
    public void UpdateSettingsPage_StableContract()
    {
        // The page reads only its own Title/Icon/Category — the VM is passed
        // through untouched — so a null VM is fine for verifying the contract
        // and avoids pulling HttpClient / UpdateChecker into the test rig.
        ISettingsPage page = new UpdateSettingsPage(null!);

        page.Title.ShouldBe("Software Updates");
        page.Icon.ShouldBe("🔄");
        page.Category.ShouldBeNull();
    }

    [Fact]
    public void GdsExportSettingsPage_StableContract()
    {
        // Renamed from "Python Environment" — the page now also hosts the
        // GenerateGdsEnabled toggle, so the broader "GDS Export" title fits.
        ISettingsPage page = new GdsExportSettingsPage(null!);

        page.Title.ShouldBe("GDS Export");
        page.Icon.ShouldBe("🐍");
        page.Category.ShouldBe("Export");
    }

    [Fact]
    public void PythonEnvironmentsSettingsPage_StableContract()
    {
        ISettingsPage page = new PythonEnvironmentsSettingsPage(null!);

        page.Title.ShouldBe("Python Environments");
        page.Icon.ShouldBe("📦");
        page.Category.ShouldBe("Export");
    }

    [Fact]
    public void AiAssistantSettingsPage_StableContract()
    {
        ISettingsPage page = new AiAssistantSettingsPage(null!);

        page.Title.ShouldBe("AI Assistant");
        page.Icon.ShouldBe("🤖");
        page.Category.ShouldBeNull();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class StubSettingsPage : ISettingsPage
    {
        public StubSettingsPage(string title) { Title = title; }
        public string Title { get; }
        public string Icon => "⚙";
        public string? Category => null;
        public object ViewModel => new object();
    }
}

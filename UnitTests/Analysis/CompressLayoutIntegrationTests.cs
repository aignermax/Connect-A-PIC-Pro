using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Integration tests for layout compression (Core + ViewModel).
/// </summary>
public class CompressLayoutIntegrationTests
{
    /// <summary>
    /// Pins the UI language to English so status-string assertions (e.g. "complete",
    /// "No components") are locale-independent — they read the localized status text
    /// (#749) which otherwise varies with the runner's OS culture.
    /// </summary>
    public CompressLayoutIntegrationTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void ViewModel_ExecutesCompressionAndUpdatesStatus()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        // Add test components
        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        var vm1 = canvas.AddComponent(comp1, "TestComp1");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 1000;
        comp2.PhysicalY = 1000;
        var vm2 = canvas.AddComponent(comp2, "TestComp2");

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        viewModel.StatusText.ShouldContain("complete");
        viewModel.ResultText.ShouldNotBeNullOrEmpty();
        viewModel.ResultText.ShouldContain("Area reduction");
    }

    [Fact]
    public void ViewModel_UpdatesComponentPositions()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        var vm1 = canvas.AddComponent(comp1, "TestComp1");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 2000;
        comp2.PhysicalY = 2000;
        var vm2 = canvas.AddComponent(comp2, "TestComp2");

        var originalVm1X = vm1.X;
        var originalVm1Y = vm1.Y;
        var originalVm2X = vm2.X;
        var originalVm2Y = vm2.Y;

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        // At least one component should have moved
        bool componentsMoved = vm1.X != originalVm1X ||
                               vm1.Y != originalVm1Y ||
                               vm2.X != originalVm2X ||
                               vm2.Y != originalVm2Y;

        componentsMoved.ShouldBeTrue();
    }

    [Fact]
    public void ViewModel_WithNoComponents_ShowsAppropriateMessage()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        viewModel.StatusText.ShouldContain("No components");
    }

    [Fact]
    public void ViewModel_RespectsLockedComponents()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        comp1.IsLocked = true; // Lock first component
        var vm1 = canvas.AddComponent(comp1, "LockedComp");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 1500;
        comp2.PhysicalY = 1500;
        var vm2 = canvas.AddComponent(comp2, "UnlockedComp");

        var originalVm1X = vm1.X;
        var originalVm1Y = vm1.Y;

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        vm1.X.ShouldBe(originalVm1X); // Locked component unchanged
        vm1.Y.ShouldBe(originalVm1Y);
        vm2.X.ShouldNotBe(1500.0); // Unlocked component moved
    }

    [Fact]
    public void ViewModel_CalculatesAreaReduction()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        // Create components with large spacing
        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        canvas.AddComponent(comp1, "Comp1");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 0;
        comp2.PhysicalY = 1500;
        canvas.AddComponent(comp2, "Comp2");

        var comp3 = TestComponentFactory.CreateBasicComponent();
        comp3.PhysicalX = 1500;
        comp3.PhysicalY = 0;
        canvas.AddComponent(comp3, "Comp3");

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        viewModel.ResultText.ShouldContain("mm²");
        viewModel.ResultText.ShouldContain("Area reduction");
        viewModel.ResultText.ShouldContain("%");
    }

    [Fact]
    public void ViewModel_PreventsConcurrentExecution()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        var comp = TestComponentFactory.CreateBasicComponent();
        canvas.AddComponent(comp, "TestComp");

        viewModel.IsCompressing = true; // Simulate ongoing compression

        // Act
        bool canExecute = viewModel.CompressLayoutCommand.CanExecute(null);

        // Assert
        canExecute.ShouldBeFalse();
    }

    [Fact]
    public void ViewModel_UpdatesProgressDuringCompression()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        // Add components far apart to ensure compression takes multiple iterations
        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        canvas.AddComponent(comp1, "Comp1");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 2000;
        comp2.PhysicalY = 2000;
        canvas.AddComponent(comp2, "Comp2");

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        // CurrentIteration should be set to MaxIterations after completion
        viewModel.CurrentIteration.ShouldBeGreaterThan(0);
        viewModel.StatusText.ShouldContain("complete");
    }

    [Fact]
    public void ViewModel_AnimatesComponentPositions()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var viewModel = new CompressLayoutViewModel();
        viewModel.Configure(canvas);

        var comp1 = TestComponentFactory.CreateBasicComponent();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        var vm1 = canvas.AddComponent(comp1, "Comp1");

        var comp2 = TestComponentFactory.CreateBasicComponent();
        comp2.PhysicalX = 2000;
        comp2.PhysicalY = 2000;
        var vm2 = canvas.AddComponent(comp2, "Comp2");

        double originalVm2X = vm2.X;
        double originalVm2Y = vm2.Y;

        // Act
        var task = viewModel.CompressLayoutCommand.ExecuteAsync(null);
        task.Wait();

        // Assert
        // ViewModel positions should be updated (animation occurred)
        bool positionsChanged = vm2.X != originalVm2X || vm2.Y != originalVm2Y;
        positionsChanged.ShouldBeTrue("Component positions should change during compression");

        // ViewModel positions should match core component positions
        vm1.X.ShouldBe(comp1.PhysicalX);
        vm1.Y.ShouldBe(comp1.PhysicalY);
        vm2.X.ShouldBe(comp2.PhysicalX);
        vm2.Y.ShouldBe(comp2.PhysicalY);
    }
}

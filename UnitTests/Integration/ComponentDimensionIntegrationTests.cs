using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Integration tests for ComponentDimensionValidator + ComponentDimensionViewModel.
/// Tests the full vertical slice from core logic through ViewModel to UI-ready data.
/// </summary>
public class ComponentDimensionIntegrationTests
{
    /// <summary>
    /// The ViewModel's StatusText is localized via LocalizationService.Instance, so pin English
    /// to keep these assertions culture-independent regardless of the CI/dev OS language.
    /// </summary>
    public ComponentDimensionIntegrationTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void DimensionValidator_DetectsIssuesInDesign_AndPopulatesViewModel()
    {
        // Arrange - Create a design with one valid and one invalid component
        var canvas = new DesignCanvasViewModel();
        var dimensionViewModel = new ComponentDimensionViewModel();
        dimensionViewModel.Configure(canvas);

        var validComponent = CreateComponentWithPins(width: 120, height: 50, pinExtents: (0, 100, 20, 30));
        var invalidComponent = CreateComponentWithPins(width: 100, height: 50, pinExtents: (0, 120, 20, 30));

        // Act - Add components to canvas (should trigger validation via collection changed)
        canvas.Components.Add(new ComponentViewModel(validComponent));
        canvas.Components.Add(new ComponentViewModel(invalidComponent));
        dimensionViewModel.RunValidationCommand.Execute(null);

        // Assert
        dimensionViewModel.HasIssues.ShouldBeTrue();
        dimensionViewModel.IssueCount.ShouldBe(1);
        dimensionViewModel.Issues.Count.ShouldBe(1);

        var issue = dimensionViewModel.Issues[0];
        issue.ComponentName.ShouldBe("TestComponent");
        issue.Issue.ShouldContain("Width");
        issue.CurrentDimensions.ShouldContain("100");
        issue.RecommendedDimensions.ShouldContain("120"); // No margin - pins at 0 to 120, need width ≥ 120
    }

    [Fact]
    public void DimensionValidator_AllValid_ShowsSuccessStatus()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var dimensionViewModel = new ComponentDimensionViewModel();
        dimensionViewModel.Configure(canvas);

        var validComponent1 = CreateComponentWithPins(width: 120, height: 50, pinExtents: (0, 100, 20, 30));
        var validComponent2 = CreateComponentWithPins(width: 150, height: 60, pinExtents: (10, 130, 25, 35));

        // Act
        canvas.Components.Add(new ComponentViewModel(validComponent1));
        canvas.Components.Add(new ComponentViewModel(validComponent2));
        dimensionViewModel.RunValidationCommand.Execute(null);

        // Assert
        dimensionViewModel.HasIssues.ShouldBeFalse();
        dimensionViewModel.IssueCount.ShouldBe(0);
        dimensionViewModel.Issues.Count.ShouldBe(0);
        dimensionViewModel.StatusText.ShouldContain("valid dimensions");
    }

    [Fact]
    public void DimensionValidator_EmptyDesign_ShowsNoIssues()
    {
        // Arrange
        var canvas = new DesignCanvasViewModel();
        var dimensionViewModel = new ComponentDimensionViewModel();
        dimensionViewModel.Configure(canvas);

        // Act
        dimensionViewModel.RunValidationCommand.Execute(null);

        // Assert
        dimensionViewModel.HasIssues.ShouldBeFalse();
        dimensionViewModel.IssueCount.ShouldBe(0);
        dimensionViewModel.StatusText.ShouldContain("valid dimensions");
    }

    [Fact]
    public void DimensionValidator_MmiLikeComponent_DetectedAndDisplayed()
    {
        // Arrange - Component with pins that extend beyond bounds
        var canvas = new DesignCanvasViewModel();
        var dimensionViewModel = new ComponentDimensionViewModel();
        dimensionViewModel.Configure(canvas);

        var mmiComponent = CreateMmi2x2LikeComponent();

        // Act
        canvas.Components.Add(new ComponentViewModel(mmiComponent));
        dimensionViewModel.RunValidationCommand.Execute(null);

        // Assert
        dimensionViewModel.HasIssues.ShouldBeTrue();
        dimensionViewModel.IssueCount.ShouldBeGreaterThan(0);

        var issue = dimensionViewModel.Issues[0];
        issue.ComponentName.ShouldBe("MMI_2x2");
        issue.CurrentDimensions.ShouldContain("120");
        issue.CurrentDimensions.ShouldContain("50");
        // Pins extend to x=130, so recommended width must be at least 130
        issue.RecommendedDimensions.ShouldContain("130");
    }

    /// <summary>
    /// Creates a component with pins at specified extents.
    /// </summary>
    private static Component CreateComponentWithPins(double width, double height,
        (double minX, double maxX, double minY, double maxY) pinExtents)
    {
        var pins = new[]
        {
            CreatePin("in", pinExtents.minX, pinExtents.minY),
            CreatePin("out", pinExtents.maxX, pinExtents.maxY)
        };

        return CreateComponent(width, height, pins);
    }

    /// <summary>
    /// Creates an MMI 2x2-like component matching the demo-pdk.json issue.
    /// </summary>
    private static Component CreateMmi2x2LikeComponent()
    {
        // Pins extend beyond component bounds (x=0 to x=130, but width is only 120)
        var pins = new[]
        {
            CreatePin("a0", 0, 12.5),
            CreatePin("a1", 0, 37.5),
            CreatePin("b0", 130, 12.5),  // Pin outside component width!
            CreatePin("b1", 130, 37.5)   // Pin outside component width!
        };

        var component = CreateComponent(120, 50, pins);
        component.Identifier = "MMI_2x2";
        return component;
    }

    private static Component CreateComponent(double width, double height, PhysicalPin[] pins)
    {
        var parts = new Part[1, 1];
        var logicalPins = new List<Pin>();
        parts[0, 0] = new Part(logicalPins);

        var sMatrix = new SMatrix(new List<Guid>(), new List<(Guid, double)>());
        var wavelengthMap = new Dictionary<int, SMatrix> { { 1550, sMatrix } };

        var component = new Component(
            wavelengthMap,
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComponent",
            DiscreteRotation.R0,
            pins.ToList());

        component.WidthMicrometers = width;
        component.HeightMicrometers = height;

        return component;
    }

    private static PhysicalPin CreatePin(string name, double x, double y)
    {
        return new PhysicalPin
        {
            Name = name,
            OffsetXMicrometers = x,
            OffsetYMicrometers = y,
            AngleDegrees = 0
        };
    }
}

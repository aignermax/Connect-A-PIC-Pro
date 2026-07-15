using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Unit tests for <see cref="DesignValidator"/>.
/// </summary>
public class DesignValidatorTests
{
    private readonly DesignValidator _validator = new();

    [Fact]
    public void Validate_EmptyConnections_ReturnsNoIssues()
    {
        var result = _validator.Validate(Array.Empty<WaveguideConnection>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NullConnections_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => _validator.Validate(null!));
    }

    [Fact]
    public void Validate_ValidConnection_ReturnsNoIssues()
    {
        var connection = CreateTestConnection();
        connection.RestoreCachedPath(CreateValidPath());

        var result = _validator.Validate(new[] { connection });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_InvalidGeometry_ReturnsIssue()
    {
        var connection = CreateTestConnection();
        var path = CreateValidPath();
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.InvalidGeometry);
        result[0].Connection.ShouldBe(connection);
    }

    [Fact]
    public void Validate_BlockedPath_ReturnsIssue()
    {
        var connection = CreateTestConnection();
        var path = CreateValidPath();
        path.IsBlockedFallback = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.BlockedPath);
    }

    [Fact]
    public void Validate_BothIssues_ReturnsTwoIssues()
    {
        var connection = CreateTestConnection();
        var path = CreateValidPath();
        path.IsInvalidGeometry = true;
        path.IsBlockedFallback = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.Type == DesignIssueType.InvalidGeometry);
        result.ShouldContain(i => i.Type == DesignIssueType.BlockedPath);
    }

    [Fact]
    public void Validate_MultipleConnections_FindsAllIssues()
    {
        var conn1 = CreateTestConnection(x1: 0, y1: 0, x2: 100, y2: 0);
        var path1 = CreateValidPath();
        path1.IsInvalidGeometry = true;
        conn1.RestoreCachedPath(path1);

        var conn2 = CreateTestConnection(x1: 200, y1: 0, x2: 300, y2: 0);
        conn2.RestoreCachedPath(CreateValidPath());

        var conn3 = CreateTestConnection(x1: 400, y1: 0, x2: 500, y2: 0);
        var path3 = CreateValidPath();
        path3.IsBlockedFallback = true;
        conn3.RestoreCachedPath(path3);

        var result = _validator.Validate(new[] { conn1, conn2, conn3 });

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Validate_IssuePosition_IsMidpointOfPins()
    {
        var connection = CreateTestConnection(x1: 100, y1: 200, x2: 300, y2: 400);
        var path = CreateValidPath();
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result[0].X.ShouldBe(200, 0.1);
        result[0].Y.ShouldBe(300, 0.1);
    }

    [Fact]
    public void Validate_IssueDescription_ContainsPinNames()
    {
        var connection = CreateTestConnection();
        var path = CreateValidPath();
        path.IsInvalidGeometry = true;
        connection.RestoreCachedPath(path);

        var result = _validator.Validate(new[] { connection });

        result[0].Description.ShouldContain("pin_a");
        result[0].Description.ShouldContain("pin_b");
    }

    [Fact]
    public void Validate_ConnectionWithNoPath_ReturnsNoIssues()
    {
        var connection = CreateTestConnection();
        // No path assigned — RoutedPath is null

        var result = _validator.Validate(new[] { connection });

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// Creates a test WaveguideConnection with two components at specified positions.
    /// </summary>
    private static WaveguideConnection CreateTestConnection(
        double x1 = 0, double y1 = 0,
        double x2 = 100, double y2 = 0)
    {
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalX = x1;
        comp1.PhysicalY = y1;
        comp1.PhysicalPins.Add(new PhysicalPin
        {
            Name = "pin_a",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            ParentComponent = comp1
        });

        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp2.PhysicalX = x2;
        comp2.PhysicalY = y2;
        comp2.PhysicalPins.Add(new PhysicalPin
        {
            Name = "pin_b",
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 0,
            ParentComponent = comp2
        });

        return new WaveguideConnection
        {
            StartPin = comp1.PhysicalPins.Last(),
            EndPin = comp2.PhysicalPins.Last()
        };
    }

    /// <summary>
    /// Creates a valid RoutedPath with a single straight segment.
    /// </summary>
    private static RoutedPath CreateValidPath()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        return path;
    }
}

/// <summary>
/// Unit tests for <see cref="DesignValidator.ValidateComponentBounds"/> — out-of-bounds detection.
/// </summary>
public class DesignValidatorBoundsTests
{
    private readonly DesignValidator _validator = new();

    /// <summary>
    /// Creates a test component at the given position with explicit 250×250 μm dimensions
    /// (1 tile at the standard grid pitch) so that bounds checks behave predictably.
    /// </summary>
    private static CAP_Core.Components.Core.Component CreateComponentAt(double x, double y)
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = x;
        comp.PhysicalY = y;
        comp.WidthMicrometers  = 250.0;
        comp.HeightMicrometers = 250.0;
        return comp;
    }

    [Fact]
    public void ValidateComponentBounds_NullComponents_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateComponentBounds(null!, 5000, 5000));
    }

    [Fact]
    public void ValidateComponentBounds_EmptyComponents_ReturnsNoIssues()
    {
        var result = _validator.ValidateComponentBounds(
            Array.Empty<CAP_Core.Components.Core.Component>(), 5000, 5000);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentBounds_ComponentInsideBounds_ReturnsNoIssues()
    {
        // Component at (0,0), 250×250 μm — fits in 5000×5000 chip
        var comp = CreateComponentAt(0, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentBounds_ComponentBeyondRightEdge_ReturnsIssue()
    {
        // Component at x=4900, width=250 → right edge at 5150 > 5000 μm chip width
        var comp = CreateComponentAt(4900, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.OutOfBounds);
    }

    [Fact]
    public void ValidateComponentBounds_ComponentBeyondBottomEdge_ReturnsIssue()
    {
        // Component at y=4900, height=250 → bottom at 5150 > 5000 μm chip height
        var comp = CreateComponentAt(0, 4900);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.OutOfBounds);
    }

    [Fact]
    public void ValidateComponentBounds_ComponentAtNegativeX_ReturnsIssue()
    {
        var comp = CreateComponentAt(-10, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.OutOfBounds);
    }

    [Fact]
    public void ValidateComponentBounds_ComponentAtNegativeY_ReturnsIssue()
    {
        // Top-edge case — symmetric with the right/bottom/left tests above.
        var comp = CreateComponentAt(0, -10);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.OutOfBounds);
    }

    [Fact]
    public void ValidateComponentBounds_ComponentExactlyAtRightEdge_ReturnsNoIssue()
    {
        // Width=250 starts at x=4750 → right edge exactly at 5000. Half-open [0, ChipWidth]
        // contract: equality is inside.
        var comp = CreateComponentAt(4750, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentBounds_MultipleComponents_FlagsOnlyOutOfBounds()
    {
        var inside  = CreateComponentAt(0, 0);
        var outside = CreateComponentAt(6000, 0); // beyond 5000 right edge

        var result = _validator.ValidateComponentBounds(
            new[] { inside, outside }, 5000, 5000);

        result.Count.ShouldBe(1);
        result[0].Connection.ShouldBeNull();
    }

    [Fact]
    public void ValidateComponentBounds_IssueDescription_ContainsChipSizeInMm()
    {
        var comp = CreateComponentAt(6000, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        // 5000 μm = 5.0 mm
        result[0].Description.ShouldContain("5.0");
    }

    [Fact]
    public void ValidateComponentBounds_IssueType_IsOutOfBounds()
    {
        var comp = CreateComponentAt(-1, 0);

        var result = _validator.ValidateComponentBounds(new[] { comp }, 5000, 5000);

        result[0].Type.ShouldBe(DesignIssueType.OutOfBounds);
    }
}

/// <summary>
/// Unit tests for <see cref="DesignValidator.ValidateComponentPdkCompatibility"/> — flags placed
/// components whose PDK no longer matches the active process (issue #570 follow-up, LC-T4).
/// </summary>
public class DesignValidatorPdkCompatibilityTests
{
    private readonly DesignValidator _validator = new();

    private static CAP_Core.Components.Core.Component CreateComponent(string name)
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.HumanReadableName = name;
        return comp;
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_NullArguments_ThrowArgumentNullException()
    {
        var comp = CreateComponent("Comp1");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?> { [comp] = "LockedLib" };
        var empty = Array.Empty<string>();

        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateComponentPdkCompatibility(null!, pdkSources, empty, empty));
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateComponentPdkCompatibility(new[] { comp }, null!, empty, empty));
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateComponentPdkCompatibility(new[] { comp }, pdkSources, null!, empty));
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateComponentPdkCompatibility(new[] { comp }, pdkSources, empty, null!));
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_PdkNotEnabledOrAgnostic_ReturnsIssue()
    {
        var comp = CreateComponent("LockedComp");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?> { [comp] = "LockedLib" };

        var result = _validator.ValidateComponentPdkCompatibility(
            new[] { comp }, pdkSources, Array.Empty<string>(), Array.Empty<string>());

        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe(DesignIssueType.PdkProcessMismatch);
        result[0].Description.ShouldContain("LockedComp");
        result[0].Description.ShouldContain("LockedLib");
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_PdkEnabled_ReturnsNoIssue()
    {
        var comp = CreateComponent("AllowedComp");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?> { [comp] = "AllowedLib" };

        var result = _validator.ValidateComponentPdkCompatibility(
            new[] { comp }, pdkSources, Array.Empty<string>(), new[] { "AllowedLib" });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_PdkProcessAgnostic_ReturnsNoIssue()
    {
        var comp = CreateComponent("ToolComp");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?> { [comp] = "Analysis Tools" };

        var result = _validator.ValidateComponentPdkCompatibility(
            new[] { comp }, pdkSources, new[] { "Analysis Tools" }, Array.Empty<string>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_UnresolvedPdkSource_ReturnsNoIssue()
    {
        // A component with no resolved PDK source (built-in, or a group with none of its own)
        // is exempt — same as SingleProcessPolicy.IsBuiltIn treats null/empty as exempt.
        var comp = CreateComponent("BuiltInComp");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?>();

        var result = _validator.ValidateComponentPdkCompatibility(
            new[] { comp }, pdkSources, Array.Empty<string>(), Array.Empty<string>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ValidateComponentPdkCompatibility_MixOfConflictedAndAllowed_FlagsOnlyConflicted()
    {
        var conflicted = CreateComponent("Conflicted");
        var allowed = CreateComponent("Allowed");
        var pdkSources = new Dictionary<CAP_Core.Components.Core.Component, string?>
        {
            [conflicted] = "LockedLib",
            [allowed] = "AllowedLib",
        };

        var result = _validator.ValidateComponentPdkCompatibility(
            new[] { conflicted, allowed }, pdkSources, Array.Empty<string>(), new[] { "AllowedLib" });

        result.Count.ShouldBe(1);
        result[0].Description.ShouldContain("Conflicted");
    }
}

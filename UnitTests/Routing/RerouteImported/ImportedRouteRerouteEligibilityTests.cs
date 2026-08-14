using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.RerouteImported;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.RerouteImported;

/// <summary>
/// Verifies which connections the "Re-route imported routes" action
/// may hand back to the live router — only frozen, unedited, optical Auto routes —
/// and that hand-edited frozen routes are surfaced as "kept unchanged" instead.
/// </summary>
public class ImportedRouteRerouteEligibilityTests
{
    private static WaveguideConnection CreateFrozenImportedConnection()
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, 100, 0, 0));
        var connection = new WaveguideConnection();
        connection.RestoreCachedPath(path);
        connection.IsRouteFrozen = true;
        return connection;
    }

    [Fact]
    public void IsEligible_FrozenAutoOpticalWithGeometry_ReturnsTrue()
    {
        var connection = CreateFrozenImportedConnection();

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeTrue();
    }

    [Fact]
    public void IsEligible_NotFrozen_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();
        connection.IsRouteFrozen = false;

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsEligible_Locked_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();
        connection.IsLocked = true;

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WaveguideType.Bend)]
    [InlineData(WaveguideType.SBend)]
    [InlineData(WaveguideType.Cobra)]
    public void IsEligible_ExplicitStyle_ReturnsFalse(WaveguideType style)
    {
        var connection = CreateFrozenImportedConnection();
        connection.Type = style;

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsEligible_HandEdited_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();
        connection.BendRadiusOverrides[0] = 25;

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsEligible_NoRoutedPath_ReturnsFalse()
    {
        var connection = new WaveguideConnection { IsRouteFrozen = true };

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsEligible_ElectricalConnection_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();
        connection.StartPin = new PhysicalPin
        {
            Name = "anode",
            LogicalPin = new Pin("anode", 0, MatterType.Electricity, RectSide.Left)
        };

        ImportedRouteRerouteEligibility.IsEligible(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsKeptHandEdited_FrozenWithManualEdits_ReturnsTrue()
    {
        var connection = CreateFrozenImportedConnection();
        connection.StraightShiftOffsets[0] = 10;

        ImportedRouteRerouteEligibility.IsKeptHandEdited(connection).ShouldBeTrue();
    }

    [Fact]
    public void IsKeptHandEdited_FrozenWithoutManualEdits_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();

        ImportedRouteRerouteEligibility.IsKeptHandEdited(connection).ShouldBeFalse();
    }

    [Fact]
    public void IsKeptHandEdited_UnfrozenWithManualEdits_ReturnsFalse()
    {
        var connection = CreateFrozenImportedConnection();
        connection.IsRouteFrozen = false;
        connection.BendRadiusOverrides[0] = 25;

        ImportedRouteRerouteEligibility.IsKeptHandEdited(connection).ShouldBeFalse();
    }

    [Fact]
    public void RouteMetricsSnapshot_Capture_SumsLengthAndBends()
    {
        var first = CreateFrozenImportedConnection();
        var second = CreateFrozenImportedConnection();

        var snapshot = RouteMetricsSnapshot.Capture(new[] { first, second });

        snapshot.LengthMicrometers.ShouldBe(200, 0.001);
        snapshot.EquivalentBends.ShouldBe(0, 0.001);
    }

    [Fact]
    public void RouteMetricsSnapshot_Capture_Empty_IsZero()
    {
        var snapshot = RouteMetricsSnapshot.Capture(Array.Empty<WaveguideConnection>());

        snapshot.LengthMicrometers.ShouldBe(0);
        snapshot.EquivalentBends.ShouldBe(0);
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Routing.MeanderGeneration;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Export equality for meandered connections (issue #1008): the Nazca/GDS export emits the
/// connection's actual meandered geometry, so the route length encoded in the exported
/// script equals the canvas length. Headless at the script level — the script IS the GDS
/// geometry source (nazca turns these exact calls into the GDS polygons on CI).
/// </summary>
public class MeanderedConnectionExportTests
{
    private const double Tolerance = 1.0;

    private static readonly Regex StraightCallRegex = new(
        @"nd\.strt\(length=(\d+(?:\.\d+)?)[^)]*\)\.put\(",
        RegexOptions.Compiled);

    private static readonly Regex BendCallRegex = new(
        @"nd\.bend\(radius=(\d+(?:\.\d+)?),\s*angle=(-?\d+(?:\.\d+)?)[^)]*\)\.put\(",
        RegexOptions.Compiled);

    [Fact]
    public void Export_MeanderedConnection_ExportedLengthEqualsCanvasLength()
    {
        var canvas = new DesignCanvasViewModel();
        var (comp1, comp2) = PlaceFacingComponents(canvas);
        ConnectStraight(canvas, comp1.PhysicalPins[0], comp2.PhysicalPins[0]);

        var connection = canvas.Connections.ShouldHaveSingleItem().Connection;
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        double target = 3.0 * connection.PathLengthMicrometers;
        var applied = new ConnectionLengthMatcher().ApplyTargetLength(
            connection, components, target, Tolerance);
        applied.IsSuccess.ShouldBeTrue(applied.FailureMessage);

        var script = new SimpleNazcaExporter().Export(canvas);

        var connectionsSection = script.Substring(
            script.IndexOf("# Waveguide Connections", StringComparison.Ordinal));
        double exportedLength = SumExportedLength(connectionsSection, out double minBendRadius);

        // The exporter rounds every number to two decimals, so each emitted segment
        // contributes at most ~0.01 µm of rounding error to the sum.
        double roundingSlack = 0.01 * connection.GetPathSegments().Count + 1e-6;
        exportedLength.ShouldBe(connection.PathLengthMicrometers, roundingSlack);
        minBendRadius.ShouldBeGreaterThanOrEqualTo(connection.BendRadiusMicrometers - 0.01);
    }

    [Fact]
    public void Export_MeanderedConnection_ExportValidatorFindsNoErrors()
    {
        var canvas = new DesignCanvasViewModel();
        var (comp1, comp2) = PlaceFacingComponents(canvas);
        ConnectStraight(canvas, comp1.PhysicalPins[0], comp2.PhysicalPins[0]);

        var connection = canvas.Connections.ShouldHaveSingleItem().Connection;
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        double target = 3.0 * connection.PathLengthMicrometers;
        var applied = new ConnectionLengthMatcher().ApplyTargetLength(
            connection, components, target, Tolerance);
        applied.IsSuccess.ShouldBeTrue(applied.FailureMessage);

        var script = new SimpleNazcaExporter().Export(canvas);
        var validator = new ExportValidator();
        var result = validator.Validate(
            canvas.Components.Select(vm => vm.Component).ToList(),
            canvas.Connections.Select(vm => vm.Connection).ToList(),
            script);

        result.IsValid.ShouldBeTrue(
            $"Validation failed with {result.FailedChecks} errors:\n" +
            string.Join("\n", result.Errors));
    }

    private static double SumExportedLength(string connectionsSection, out double minBendRadius)
    {
        var ci = CultureInfo.InvariantCulture;
        double total = 0.0;
        minBendRadius = double.MaxValue;

        foreach (Match match in StraightCallRegex.Matches(connectionsSection))
        {
            total += double.Parse(match.Groups[1].Value, ci);
        }

        foreach (Match match in BendCallRegex.Matches(connectionsSection))
        {
            double radius = double.Parse(match.Groups[1].Value, ci);
            double sweepDegrees = double.Parse(match.Groups[2].Value, ci);
            total += radius * Math.Abs(sweepDegrees) * Math.PI / 180.0;
            minBendRadius = Math.Min(minBendRadius, radius);
        }

        return total;
    }

    private static (Component Comp1, Component Comp2) PlaceFacingComponents(DesignCanvasViewModel canvas)
    {
        var comp1 = CreateTestComponent("MMI_1x2", 0, 0, 100, 50);
        comp1.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out1",
            ParentComponent = comp1,
            OffsetXMicrometers = 100,
            OffsetYMicrometers = 25,
            AngleDegrees = 0
        });
        canvas.AddComponent(comp1, "MMI_1x2");

        var comp2 = CreateTestComponent("Detector", 250, 0, 50, 50);
        comp2.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in",
            ParentComponent = comp2,
            OffsetXMicrometers = 0,
            OffsetYMicrometers = 25,
            AngleDegrees = 180
        });
        canvas.AddComponent(comp2, "Detector");
        return (comp1, comp2);
    }

    private static void ConnectStraight(
        DesignCanvasViewModel canvas, PhysicalPin startPin, PhysicalPin endPin)
    {
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, startY, endX, endY, startPin.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
    }

    private static Component CreateTestComponent(
        string identifier, double x, double y, double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: $"test_{identifier.ToLower()}",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: identifier,
            rotationCounterClock: DiscreteRotation.R0,
            physicalPins: new List<PhysicalPin>());

        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;
        component.NazcaOriginOffsetX = 0;
        component.NazcaOriginOffsetY = height;
        return component;
    }
}

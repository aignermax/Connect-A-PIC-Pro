using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Envelope-carrying route cells end-to-end (field report,
/// curves_nazca_partial.gds): gdsfactory straight/bend cells with a DEVREC-style
/// (68,0) envelope polygon must DISSOLVE into the route geometry — the chain
/// between two devices comes back as ONE real, re-routable connection, not as a
/// pile of bogus component drafts. The fixture rotates the chain 94.02° so the
/// exact-rotation placement path is covered too. Coordinates are hand-computed
/// (nm) so every joint abuts exactly.
/// </summary>
public class GdsEnvelopeRouteDissolveIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsenv-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>
    /// dev.out — straight(94.02°) — bend94(90°) — straight(94.02°) — dev.in.
    /// Every route cell carries an envelope polygon fully wrapping its core —
    /// the shape gdsfactory emits. Same joint coordinates as the draft-path
    /// fan-out fixture: J1 = (10000, 2000), J2 = (29951, 23401), J3 = (29253, 33377) nm.
    /// </summary>
    private static byte[] EnvelopeChainLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("dev", 0, 0)
            .SRef("bend94_route", 10000, 22000, angleDegrees: 90)
            .SRef("straight_route", 31946, 23541, angleDegrees: 94.02)
            .SRef("dev", 31248, 33517, angleDegrees: 94.02)
        .EndCell()
        .BeginCell("dev")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .Text(1, 10, "in", 0, 2000)
            .Text(1, 10, "out", 10000, 2000)
        .EndCell()
        .BeginCell("bend94_route")
            .Boundary(1, 0, AnnulusPoints(0, 0, 20250, 19750, 180, 274.02))
            .Boundary(68, 0, (-20750, -20698), (1919, -20698), (1919, 500), (-20750, 500), (-20750, -20698))
        .EndCell()
        .BeginCell("straight_route")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
        .EndCell()
        .EndLibrary()
        .ToArray();

    /// <summary>A tessellated annulus sector outline in nm (gdsfactory bend_circular shape).</summary>
    private static (int X, int Y)[] AnnulusPoints(int centerX, int centerY,
        int radiusOuter, int radiusInner, double fromDegrees, double toDegrees, int segments = 48)
    {
        var points = new List<(int, int)>();
        for (int i = 0; i <= segments; i++)
        {
            double phi = (fromDegrees + ((toDegrees - fromDegrees) * i / segments)) * Math.PI / 180.0;
            points.Add(((int)Math.Round(centerX + radiusOuter * Math.Cos(phi)),
                        (int)Math.Round(centerY + radiusOuter * Math.Sin(phi))));
        }
        for (int i = segments; i >= 0; i--)
        {
            double phi = (fromDegrees + ((toDegrees - fromDegrees) * i / segments)) * Math.PI / 180.0;
            points.Add(((int)Math.Round(centerX + radiusInner * Math.Cos(phi)),
                        (int)Math.Round(centerY + radiusInner * Math.Sin(phi))));
        }
        points.Add(points[0]);
        return points.ToArray();
    }

    [Fact]
    public async Task Import_EnvelopeRouteChain_DissolvesIntoOneRealConnection()
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "envchain.gds");
        File.WriteAllBytes(gdsPath, EnvelopeChainLibrary());

        var service = _host.CreateService();
        var canvas = new DesignCanvasViewModel();
        var executor = new GdsPlacementExecutor(
            canvas, new CommandManager(), () => _host.Templates.ToList());
        var vm = new GdsImportDialogViewModel(gdsPath, service, executor);
        await vm.StartAnalysisAsync();
        vm.HasError.ShouldBeFalse(vm.ErrorText);

        await vm.ImportCommand.ExecuteAsync(null);

        vm.HasError.ShouldBeFalse(vm.ErrorText);
        var group = canvas.Components.ShouldHaveSingleItem().Component
            .ShouldBeOfType<ComponentGroup>();

        // Only the two devices survive as components — every route cell dissolved.
        group.ChildComponents.Count.ShouldBe(2,
            "the straight/bend route cells must dissolve into route geometry, not become components");

        // The whole chain is ONE real pinned connection from dev.out to dev.in.
        var connection = group.InternalPaths.ShouldHaveSingleItem();
        connection.StartPin.ShouldNotBeNull();
        connection.EndPin.ShouldNotBeNull();
        connection.StartPin!.Name.ShouldBe("out");
        connection.EndPin!.Name.ShouldBe("in");

        // The connection's geometry spans the whole chain: it starts exactly at
        // dev.out and ends exactly at dev.in (the dissolved route geometry
        // anchored the reconstruction; re-routing keeps the pin endpoints).
        var start = connection.StartPin.GetAbsolutePosition();
        var end = connection.EndPin.GetAbsolutePosition();
        var firstSegment = connection.Path.Segments.First();
        var lastSegment = connection.Path.Segments.Last();
        DistSq(firstSegment.StartPoint.X, firstSegment.StartPoint.Y, start.x, start.y)
            .ShouldBeLessThan(0.05 * 0.05, "the route must start on the start pin");
        DistSq(lastSegment.EndPoint.X, lastSegment.EndPoint.Y, end.x, end.y)
            .ShouldBeLessThan(0.05 * 0.05, "the route must end on the end pin");

        // The second device keeps its exact 94.02° rotation (app 265.98°).
        group.ChildComponents.ShouldContain(c => Math.Abs(c.RotationDegrees - 265.98) < 0.01);
    }

    private static double DistSq(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return dx * dx + dy * dy;
    }
}

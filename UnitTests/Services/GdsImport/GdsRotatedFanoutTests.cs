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
/// Rotated fan-out regression: a chain of gdsfactory route cells (straight →
/// partial-angle bend → straight, at 94.02°) between two devices must
/// reconstruct into REAL connections end to end. Two geometry traps made this
/// fail in the field: the bend's layer-68 envelope inflates its bbox past the
/// port faces (edge-touch scan loses the exit pin), and the partial-angle exit
/// face is tilted past every axis-aligned edge (only the terminus ring scan
/// sees it). The fixtures carry both: envelope polygons plus a 94.02° annulus
/// bend. Coordinates are hand-computed (nm) so every joint abuts exactly.
/// </summary>
public class GdsRotatedFanoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsfanout-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>
    /// dev.out —(joint J1)— bend94(90°) —(joint J2)— straight(94.02°) —(joint J3)— dev.in.
    /// J1 = (10000, 2000), J2 = (29951, 23401), J3 = (29253, 33377) nm; the bend's
    /// exit face is tilted 4.02° (94.02° sweep), the straight and the second device
    /// are rotated to match (dev2 at 94.02° so its west-facing 'in' pin looks back
    /// down the straight's 94.02° exit — anti-parallel, not parallel).
    /// </summary>
    private static byte[] RotatedFanoutLibrary() => GdsTestWriter.Create()
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
            .Boundary(68, 0, (2500, 500), (3000, 500), (3000, 1000), (2500, 1000), (2500, 500))
        .EndCell()
        .BeginCell("straight_route")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(111, 0, (0, 3000), (500, 3000), (500, 3500), (0, 3500), (0, 3000))
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
    public async Task Import_RotatedFanoutChain_ReconstructsEveryJoint()
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "fanout.gds");
        File.WriteAllBytes(gdsPath, RotatedFanoutLibrary());

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
        group.ChildComponents.Count.ShouldBe(4, "two devices + bend draft + straight draft");

        // Exact rotations survive: the bend at 90° (app 270), the straight and
        // the second device at 94.02° (app 265.98).
        group.ChildComponents.ShouldContain(c => Math.Abs(c.RotationDegrees - 270) < 0.01);
        group.ChildComponents.Count(c => Math.Abs(c.RotationDegrees - 265.98) < 0.01)
            .ShouldBe(2, "straight and second device both sit at 94.02° (app 265.98)");

        // Every joint reconstructs as a REAL pinned connection — nothing falls
        // back to pin-less frozen geometry (the field failure: chain pieces
        // arrived as misplaced frozen paths instead of connections).
        var pinned = group.InternalPaths.Where(p => p.StartPin is not null && p.EndPin is not null).ToList();
        pinned.Count.ShouldBe(3, "dev.out→bend, bend→straight, straight→dev.in must all connect");
        group.InternalPaths.Count(p => p.StartPin is null || p.EndPin is null)
            .ShouldBe(0, "no route geometry may be left over as frozen paths");

        // The placed pins of every connection coincide (the placement rotation
        // math lands exactly on the projected joints).
        foreach (var path in pinned)
        {
            var start = path.StartPin!.GetAbsolutePosition();
            var end = path.EndPin!.GetAbsolutePosition();
            double dx = start.x - end.x;
            double dy = start.y - end.y;
            Math.Sqrt(dx * dx + dy * dy).ShouldBeLessThan(0.05,
                $"connection endpoints must coincide within the abutment tolerance");
        }
    }
}

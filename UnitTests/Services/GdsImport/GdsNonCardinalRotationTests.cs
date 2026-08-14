using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Non-Manhattan import regression: an instance rotated by a NON-cardinal
/// angle must keep its exact rotation — snapping to the nearest 90° moves its
/// pins microns off the true joint and breaks the reconstructed connections
/// (field report: a rotated fan-out collapsed into blocked/overlapping paths).
/// Fixture: two waveguide cells, the second rotated 30°, abutting exactly.
/// </summary>
public class GdsNonCardinalRotationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsrot-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>
    /// Two 10 µm waveguides end-to-end, BOTH rotated 30° — a kink-free joint
    /// needs anti-parallel pins, so the second segment keeps the first's
    /// rotation. Instance 2's reference point (8660, 5000) puts its left pin
    /// within 0.3 nm of instance 1's right pin: R(30°)·(10000,2000) ≈
    /// (7660.25, 6732.05) for the joint.
    /// </summary>
    private static byte[] RotatedJointLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wg", 0, 0, angleDegrees: 30)
            .SRef("wg", 8660, 5000, angleDegrees: 30)
        .EndCell()
        .BeginCell("wg")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .Text(1, 10, "in", 0, 2000)
            .Text(1, 10, "out", 10000, 2000)
        .EndCell()
        .EndLibrary()
        .ToArray();

    [Fact]
    public async Task Import_NonCardinalInstance_KeepsExactRotation_AndConnects()
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "rotated.gds");
        File.WriteAllBytes(gdsPath, RotatedJointLibrary());

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
            .ShouldBeOfType<CAP_Core.Components.Core.ComponentGroup>();
        group.ChildComponents.Count.ShouldBe(2);

        // GDS +30° ≡ app −30° ≡ 330° (Y-flip) — kept exactly for BOTH instances,
        // not snapped to 0°: snapping would move their pins off the true joint.
        group.ChildComponents.ShouldAllBe(c => Math.Abs(c.RotationDegrees - 330.0) < 0.5);

        // The joint must reconstruct: one connection between the two instances.
        group.InternalPaths.Count(p => p.StartPin is not null && p.EndPin is not null)
            .ShouldBe(1, "the exactly-abutting pins must reconstruct into one connection");
    }
}

using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

/// <summary>
/// Verifies the local prerequisite gating of <see cref="Tidy3dSMatrixService"/>.
/// No test here touches Python or the network — the service must fail fast with
/// actionable messages before anything is launched.
/// </summary>
public class Tidy3dSMatrixServiceTests
{
    private static string ExistingScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tidy3d_sparams_{Guid.NewGuid():N}.py");
        File.WriteAllText(path, "# test stub");
        return path;
    }

    [Fact]
    public async Task CheckAvailability_WithoutApiKey_ExplainsHowToFix()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => null);

            var result = await service.CheckAvailabilityAsync();

            result.IsAvailable.ShouldBeFalse();
            result.Message.ShouldContain("API key");
            result.Message.ShouldContain("Settings");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task CheckAvailability_WithMissingScript_ReportsScriptPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist", "tidy3d_sparams.py");
        var service = new Tidy3dSMatrixService("python3", missing, () => "key-123");

        var result = await service.CheckAvailabilityAsync();

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("tidy3d_sparams.py");
    }

    [Fact]
    public async Task Solve_WithoutGeometry_FailsWithoutLaunchingAnything()
    {
        var service = new Tidy3dSMatrixService("python3", "irrelevant.py", () => "key-123");

        var result = await service.SolveAsync(new FdtdSMatrixRequest());

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("geometry");
    }

    [Fact]
    public async Task Solve_WithoutApiKey_FailsWithMissingDependency()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => "  ");

            var result = await service.SolveAsync(new FdtdSMatrixRequest
            {
                Polygons = new List<FdtdPolygon> { new() },
            });

            result.Success.ShouldBeFalse();
            result.MissingDependency.ShouldBe("tidy3d");
            result.Error.ShouldContain("API key");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public async Task EstimateCost_WithoutApiKey_Fails()
    {
        var script = ExistingScript();
        try
        {
            var service = new Tidy3dSMatrixService("python3", script, () => string.Empty);

            var estimate = await service.EstimateCostAsync(new FdtdSMatrixRequest());

            estimate.Success.ShouldBeFalse();
            estimate.Error.ShouldContain("API key");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void Service_ImplementsCostEstimatorCapability()
    {
        var service = new Tidy3dSMatrixService("python3", "s.py", () => null);

        service.ShouldBeAssignableTo<IFdtdSMatrixService>();
        service.ShouldBeAssignableTo<IFdtdCostEstimator>();
    }
}

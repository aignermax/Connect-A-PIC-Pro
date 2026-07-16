using System;
using System.Linq;
using CAP_Core.Solvers.Fdtd;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

/// <summary>
/// Verifies the FDTD sweep planner (issue #582): the planned uniform grid must
/// hit every wavelength the component already defines, so the recompute
/// overwrites all of them instead of leaving e.g. 980/1310 nm stale.
/// </summary>
public class FdtdWavelengthPlannerTests
{
    /// <summary>Wavelengths (nm) produced by a linspace over the plan, rounded like the converter does.</summary>
    private static int[] GridNm(FdtdWavelengthPlan plan)
    {
        if (plan.Points == 1)
            return new[] { (int)Math.Round(plan.StartUm * 1000.0) };
        double step = (plan.StopUm - plan.StartUm) / (plan.Points - 1);
        return Enumerable.Range(0, plan.Points)
            .Select(i => (int)Math.Round((plan.StartUm + i * step) * 1000.0))
            .ToArray();
    }

    [Fact]
    public void Plan_NoExistingWavelengths_UsesDefaultBand()
    {
        var plan = FdtdWavelengthPlanner.Plan(Array.Empty<int>());

        plan.StartUm.ShouldBe(FdtdWavelengthPlanner.DefaultStartUm);
        plan.StopUm.ShouldBe(FdtdWavelengthPlanner.DefaultStopUm);
        plan.Points.ShouldBe(FdtdWavelengthPlanner.DefaultPoints);
        plan.UncoveredNm.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_SiepicWavelengths_CoversAllThreeExactly()
    {
        // The #582 repro: SiEPIC components are defined at 980/1310/1550 nm but the
        // old fixed 1500–1600 nm sweep left 980 and 1310 stale.
        var plan = FdtdWavelengthPlanner.Plan(new[] { 1550, 980, 1310 });

        plan.StartUm.ShouldBe(0.98, 1e-9);
        plan.StopUm.ShouldBe(1.55, 1e-9);
        plan.Points.ShouldBe(20); // gcd(330, 570) = 30 nm step over 570 nm span
        plan.UncoveredNm.ShouldBeEmpty();

        var grid = GridNm(plan);
        grid.ShouldContain(980);
        grid.ShouldContain(1310);
        grid.ShouldContain(1550);
    }

    [Fact]
    public void Plan_SingleWavelength_CentresBandOnIt()
    {
        var plan = FdtdWavelengthPlanner.Plan(new[] { 1550 });

        plan.Points.ShouldBe(FdtdWavelengthPlanner.SingleWavelengthPoints);
        plan.UncoveredNm.ShouldBeEmpty();
        GridNm(plan).ShouldContain(1550); // odd point count → centre hits it exactly
    }

    [Fact]
    public void Plan_TwoWavelengths_SweepsExactlyBetweenThem()
    {
        var plan = FdtdWavelengthPlanner.Plan(new[] { 1500, 1600 });

        plan.StartUm.ShouldBe(1.5, 1e-9);
        plan.StopUm.ShouldBe(1.6, 1e-9);
        plan.Points.ShouldBe(2); // gcd grid = the two endpoints themselves
        plan.UncoveredNm.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_CoprimeSpacings_CapsPointsAndReportsUncovered()
    {
        // gcd(1, 619) = 1 → exact coverage would need 621 points; the cap kicks in
        // and the wavelength the capped grid misses is reported, not silently stale.
        var plan = FdtdWavelengthPlanner.Plan(new[] { 980, 981, 1600 });

        plan.Points.ShouldBe(FdtdWavelengthPlanner.MaxPoints);
        plan.StartUm.ShouldBe(0.98, 1e-9);
        plan.StopUm.ShouldBe(1.6, 1e-9);
        plan.UncoveredNm.ShouldContain(981);
        plan.UncoveredNm.ShouldNotContain(980); // endpoints are always on the grid
        plan.UncoveredNm.ShouldNotContain(1600);
    }

    [Fact]
    public void Plan_IgnoresDuplicatesAndNonPositiveValues()
    {
        var plan = FdtdWavelengthPlanner.Plan(new[] { 1550, 1550, 0, -3 });

        // Only 1550 remains → single-wavelength band.
        plan.Points.ShouldBe(FdtdWavelengthPlanner.SingleWavelengthPoints);
        GridNm(plan).ShouldContain(1550);
    }

    [Fact]
    public void Plan_NeverExceedsMaxPoints()
    {
        var plan = FdtdWavelengthPlanner.Plan(new[] { 400, 403, 2000 });

        plan.Points.ShouldBeLessThanOrEqualTo(FdtdWavelengthPlanner.MaxPoints);
    }
}

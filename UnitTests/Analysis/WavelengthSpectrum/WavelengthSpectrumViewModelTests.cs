using System;
using System.Threading.Tasks;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.WavelengthSpectrum;

public class WavelengthSpectrumViewModelTests
{
    private static WavelengthSpectrumViewModel CreateVm()
    {
        var vm = new WavelengthSpectrumViewModel { AutoRefreshDelay = TimeSpan.Zero };
        vm.Configure(new DesignCanvasViewModel());
        return vm;
    }

    [Fact]
    public void ParameterChange_BeforeFirstSweep_DoesNotAutoRefresh()
    {
        var vm = CreateVm();

        vm.StartNm = 1400;

        vm.PendingAutoRefresh.ShouldBeNull();
    }

    [Fact]
    public async Task ParameterChange_AfterFirstSweep_ReRunsAutomatically()
    {
        var vm = CreateVm();
        vm.HasResult = true; // simulate a completed first sweep

        vm.EndNm = 1650;

        vm.PendingAutoRefresh.ShouldNotBeNull();
        await vm.PendingAutoRefresh!;
        // The empty canvas makes the auto-triggered sweep report "no circuit" —
        // proof that the sweep pipeline actually re-ran on the parameter change.
        vm.StatusText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task EachParameter_TriggersAutoRefresh()
    {
        var vm = CreateVm();
        vm.HasResult = true;

        vm.StartNm = 1400;
        var first = vm.PendingAutoRefresh;
        first.ShouldNotBeNull();
        await first!;

        vm.StepCount = 50;
        var second = vm.PendingAutoRefresh;
        second.ShouldNotBeNull();
        await second!;
    }

    [Fact]
    public async Task RunSweep_InvalidRange_ReportsValidationError()
    {
        var vm = CreateVm();
        vm.StartNm = 1600;
        vm.EndNm = 1500;

        await vm.RunSweepCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("wavelength");
    }

    [Fact]
    public async Task RunSweep_EmptyCanvas_ReportsNoCircuit()
    {
        var vm = CreateVm();

        await vm.RunSweepCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldNotBeNullOrEmpty();
        vm.IsSweeping.ShouldBeFalse();
    }
}

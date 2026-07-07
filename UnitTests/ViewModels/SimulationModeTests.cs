using System.Threading.Tasks;
using CAP.Avalonia.ViewModels.Analysis;
using CommunityToolkit.Mvvm.Input;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

public class SimulationModeTests
{
    [Fact]
    public void MainViewModel_DefaultsToCwMode()
    {
        var vm = UnitTests.Helpers.MainViewModelTestHelper.CreateMainViewModel();
        vm.SimulationMode.ShouldBe(SimulationMode.Cw);
    }

    [Fact]
    public async Task Run_InTransientMode_OpensAnalysisDock_NotCwOverlay()
    {
        var vm = UnitTests.Helpers.MainViewModelTestHelper.CreateMainViewModel();
        vm.SimulationMode = SimulationMode.Transient;

        await ((IAsyncRelayCommand)vm.RunSimulationCommand).ExecuteAsync(null);

        vm.BottomPanel.Analysis.IsVisible.ShouldBeTrue();
        vm.BottomPanel.Analysis.SelectedTabIndex.ShouldBe(0);
        vm.Canvas.ShowPowerFlow.ShouldBeFalse();
    }
}

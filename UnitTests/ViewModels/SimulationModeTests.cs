using CAP.Avalonia.ViewModels.Analysis;
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
}

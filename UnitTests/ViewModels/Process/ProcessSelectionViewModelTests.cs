using System.Linq;
using CAP.Avalonia.ViewModels.Process;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Process;

public class ProcessSelectionViewModelTests
{
    private static ProcessGroup Soi => new("SOI 220",
        new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo", "SiEPIC" });

    [Fact]
    public void Choices_ListGroupsPlusPlayground()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.Choices.Count.ShouldBe(2);
        vm.Choices.Last().IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public void Confirm_WithGroup_SetsRealProcessResult()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.SelectedChoice = vm.Choices.First();
        vm.ConfirmCommand.Execute(null);
        vm.Result!.DisplayName.ShouldBe("SOI 220");
        vm.Result.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void Confirm_WithoutSelection_DoesNothing()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.CanConfirm.ShouldBeFalse();
        vm.ConfirmCommand.Execute(null);
        vm.Result.ShouldBeNull();
    }
}

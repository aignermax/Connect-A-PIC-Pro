using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewPdkReopenGuardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-vm-reopen-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
            new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 }
        }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private NewComponentViewModel Build(UserPdkStore store)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return vm;
    }

    private static void SimulateComboBoxReselectOnItemsSourceSwap(NewComponentViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.PdkChoices)) return;

            var previouslyHeld = vm.SelectedPdkChoice;
            vm.SelectedPdkChoice = null;
            if (previouslyHeld is not null && vm.PdkChoices.Contains(previouslyHeld))
            {
                vm.SelectedPdkChoice = previouslyHeld;
            }
        };
    }

    [Fact]
    public async Task SelectingTheSentinel_withComboBoxReplayAfterRefresh_invokesCreateNewPdkExactlyOnce()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Existing Lib", process, "gdsfactory", null);
        var vm = Build(store);
        SimulateComboBoxReselectOnItemsSourceSwap(vm);

        var callCount = 0;
        vm.CreateNewPdk = () =>
        {
            callCount++;
            var path = store.CreateNamedPdkWithProcess("Brand New Lib", process, "gdsfactory", null);
            return Task.FromResult<UserPdkInfo?>(new UserPdkInfo("Brand New Lib", path, process));
        };

        vm.SelectedPdkChoice = vm.PdkChoices[^1];
        await Task.Yield();
        await Task.Yield();

        callCount.ShouldBe(1);
        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SelectedCustomPdk!.Name.ShouldBe("Brand New Lib");
        vm.SelectedPdkChoice.ShouldNotBeNull();
        vm.SelectedPdkChoice!.IsNewPdk.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingTheSentinel_whenTheCreatedPdkCannotBeFoundAfterRefresh_fallsBackInsteadOfStayingOnTheSentinel()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Existing Lib", process, "gdsfactory", null);
        var vm = Build(store);
        var existingChoice = vm.PdkChoices[0];

        vm.CreateNewPdk = () => Task.FromResult<UserPdkInfo?>(
            new UserPdkInfo("Ghost Lib", Path.Combine(_root, "ghost.json"), process));

        vm.SelectedPdkChoice = vm.PdkChoices[^1];
        await Task.Yield();

        vm.SelectedPdkChoice.ShouldNotBeNull();
        vm.SelectedPdkChoice!.IsNewPdk.ShouldBeFalse();
        vm.SelectedPdkChoice.ShouldBe(existingChoice);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

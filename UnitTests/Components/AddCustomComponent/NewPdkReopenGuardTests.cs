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

/// <summary>
/// Regression coverage for the "New PDK…" reopen bug: after a successful modal creation,
/// <see cref="NewComponentViewModel.PdkSelection"/>'s <c>RefreshPdkChoices</c> rebuilds
/// <see cref="NewComponentViewModel.PdkChoices"/> and raises its property-changed
/// notification. A bound <c>ComboBox</c> reacts to that <c>ItemsSource</c> swap by clearing
/// <c>SelectedItem</c> and then reselecting whatever it previously held, if that object is
/// still present (by reference) in the new source — and since
/// <see cref="PdkChoice.NewPdkSentinel"/> is a single shared static instance, it always is.
/// That replay used to reselect the sentinel a second time, and because it happens
/// synchronously nested inside the creation handler (after its <c>IsBusy</c> guard has
/// already been lifted), the "new PDK" modal reopened immediately. These tests simulate that
/// ComboBox replay directly against the view model (no Avalonia control needed) and assert
/// the modal-creation hook fires exactly once.
/// </summary>
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

    /// <summary>
    /// Wires a <see cref="PropertyChanged"/> listener that reproduces the bound ComboBox's
    /// real reaction to an <c>ItemsSource</c> swap: clear the selection, then reselect the
    /// previously-held item if it is still present in the new source.
    /// </summary>
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

        vm.SelectedPdkChoice = vm.PdkChoices[^1]; // select the sentinel
        await Task.Yield();
        await Task.Yield(); // pump any nested re-fired handler too

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
        var existingChoice = vm.PdkChoices[0]; // ctor already pre-selected this one

        // The hook reports a path that ListCustomPdks() will never see (e.g. an out-of-store
        // location or a save that failed after all) -- RefreshPdkChoices' FirstOrDefault(...)
        // legitimately returns null.
        vm.CreateNewPdk = () => Task.FromResult<UserPdkInfo?>(
            new UserPdkInfo("Ghost Lib", Path.Combine(_root, "ghost.json"), process));

        vm.SelectedPdkChoice = vm.PdkChoices[^1]; // select the sentinel
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

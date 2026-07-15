using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class PreviewEditFlowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-vm-e2e-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult OkWithPolygon() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (8, 0), (8, 1.5), (0, 1.5) } }
        },
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
            new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 }
        }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private NewComponentViewModel Build(UserPdkStore store, IReadOnlyList<ProcessDefinition> processes)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(OkWithPolygon());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        return new NewComponentViewModel(extractor, fdtd: null, store, processes);
    }

    [Fact]
    public async Task RunPreview_withMockedPolygonResult_setsHasPreviewTrue()
    {
        var process = new ProcessDefinition { Name = "P" };
        var vm = Build(Store(), new List<ProcessDefinition> { process });
        vm.ComponentName = "My Comp";
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.HasPreview.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadForEdit_thenSave_overwritesTheOriginalComponent_withoutDuplicating()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, new PdkComponentDraft
        {
            Name = "comp1", WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = rawCode, RawCodeBackend = "gdsfactory",
            Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
        });
        var vm = Build(store, new List<ProcessDefinition> { process });
        vm.LoadForEdit(new ComponentTemplate { Name = "comp1", RawCode = rawCode, RawCodeBackend = "gdsfactory", PdkSource = "Lib" });

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Components.Count(c => c.Name == "comp1").ShouldBe(1);
    }

    [Fact]
    public async Task SelectingTheSentinel_withReselectReplayAfterRefresh_invokesCreateNewPdkExactlyOnce()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Existing Lib", process, "gdsfactory", null);
        var vm = Build(store, new List<ProcessDefinition> { process });
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
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

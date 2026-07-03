using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings.InstanceOverride;

/// <summary>
/// Tests for the per-instance override backend toggle (issue #637): switching between
/// Nazca and gdsfactory swaps the help texts and stub, routes Run Preview to the
/// matching preview service, and Apply records the backend on the stored override.
/// </summary>
public class InstanceOverrideBackendToggleTests
{
    private const string TemplateCode = "def component():\n    return pdk.strt()\n";

    private static Mock<NazcaComponentPreviewService> MockNazcaService()
        => new(MockBehavior.Loose,
            "python3", "preview.py", (TimeSpan?)TimeSpan.FromSeconds(5), (ProcessLaunchFactory?)null) { CallBase = false };

    private static Mock<GdsFactoryComponentPreviewService> MockGdsFactoryService()
        => new(MockBehavior.Loose,
            "python3", "gf_preview.py", (TimeSpan?)TimeSpan.FromSeconds(5), (ProcessLaunchFactory?)null) { CallBase = false };

    private static NazcaPreviewResult OkResult() => new()
    {
        Success = true,
        XMin = 0, YMin = 0, XMax = 12, YMax = 6,
        Pins = new List<NazcaPreviewPin>()
    };

    private static InstanceNazcaCodeEditorViewModel BuildVm(
        NazcaComponentPreviewService nazcaService,
        GdsFactoryComponentPreviewService? gdsFactoryService = null,
        Dictionary<string, NazcaCodeOverride>? store = null)
    {
        return new InstanceNazcaCodeEditorViewModel(
            componentKey: "comp-1",
            storedOverrides: store ?? new Dictionary<string, NazcaCodeOverride>(),
            liveComponent: null,
            moduleName: "demo",
            nazcaFunction: "mmi2x2_dp",
            nazcaParameters: null,
            templateCode: TemplateCode,
            previewService: nazcaService,
            gdsFactoryPreviewService: gdsFactoryService);
    }

    [Fact]
    public void Toggle_SwapsBackendTextsAndInvalidatesPreview()
    {
        var vm = BuildVm(MockNazcaService().Object);
        vm.BackendTexts.ShouldBeSameAs(OverrideBackendTexts.Nazca);

        vm.IsGdsFactoryBackend = true;

        vm.BackendTexts.ShouldBeSameAs(OverrideBackendTexts.GdsFactory);
        vm.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Toggle_ReplacesUntouchedStubButKeepsUserCode()
    {
        var vm = BuildVm(MockNazcaService().Object);

        // Untouched Nazca stub → swapped to the gdsfactory stub.
        vm.Code = OverrideBackendTexts.Nazca.Stub;
        vm.IsGdsFactoryBackend = true;
        vm.Code.ShouldBe(OverrideBackendTexts.GdsFactory.Stub);

        // Real user code must never be discarded by the toggle.
        vm.Code = "def component():\n    return my_cell()\n";
        vm.IsGdsFactoryBackend = false;
        vm.Code.ShouldBe("def component():\n    return my_cell()\n");
    }

    [Fact]
    public async Task RunPreview_GdsFactoryBackend_UsesGdsFactoryService()
    {
        var nazcaMock = MockNazcaService();
        var gfMock = MockGdsFactoryService();
        gfMock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult());
        var vm = BuildVm(nazcaMock.Object, gfMock.Object);
        vm.IsGdsFactoryBackend = true;
        vm.Code = "def component():\n    return gf.Component()\n";

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeTrue();
        gfMock.Verify(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        nazcaMock.Verify(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunPreview_GdsFactoryBackendWithoutService_ReportsError()
    {
        var vm = BuildVm(MockNazcaService().Object, gdsFactoryService: null);
        vm.IsGdsFactoryBackend = true;

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeFalse();
        vm.PreviewError.ShouldContain("gdsfactory");
    }

    [Fact]
    public async Task Apply_GdsFactoryBackend_RecordsBackendOnStoredOverride()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var gfMock = MockGdsFactoryService();
        gfMock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult());
        var vm = BuildVm(MockNazcaService().Object, gfMock.Object, store);
        vm.IsGdsFactoryBackend = true;
        vm.Code = "def component():\n    return gf.Component()\n";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        store["comp-1"].Backend.ShouldBe(OverrideBackend.GdsFactory);
    }

    [Fact]
    public async Task Apply_NazcaBackend_LeavesBackendNullForCompatibility()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var nazcaMock = MockNazcaService();
        nazcaMock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult());
        var vm = BuildVm(nazcaMock.Object, store: store);
        vm.Code = "def component():\n    return pdk.custom()\n";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        store["comp-1"].Backend.ShouldBeNull();
    }

    [Fact]
    public void Constructor_StoredGdsFactoryOverride_SeedsToggle()
    {
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride
            {
                RawCode = "def component():\n    return gf.Component()\n",
                Backend = OverrideBackend.GdsFactory,
            }
        };

        var vm = BuildVm(MockNazcaService().Object, store: store);

        vm.IsGdsFactoryBackend.ShouldBeTrue();
        vm.BackendTexts.ShouldBeSameAs(OverrideBackendTexts.GdsFactory);
        vm.Code.ShouldContain("gf.Component()");
    }

    [Fact]
    public async Task Reset_RestoresNazcaBackend()
    {
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride
            {
                RawCode = "def component():\n    return gf.Component()\n",
                Backend = OverrideBackend.GdsFactory,
            }
        };
        var mock = MockNazcaService();
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult());
        var vm = BuildVm(mock.Object, store: store);
        vm.IsGdsFactoryBackend.ShouldBeTrue();

        await vm.ResetToTemplateCommand.ExecuteAsync(null);

        vm.IsGdsFactoryBackend.ShouldBeFalse();
        vm.BackendTexts.ShouldBeSameAs(OverrideBackendTexts.Nazca);
        store.ShouldNotContainKey("comp-1");
    }

    [Fact]
    public void OverrideBackend_IsGdsFactory_NullAndNazcaAreFalse()
    {
        OverrideBackend.IsGdsFactory(null).ShouldBeFalse();
        OverrideBackend.IsGdsFactory(OverrideBackend.Nazca).ShouldBeFalse();
        OverrideBackend.IsGdsFactory("GDSFACTORY").ShouldBeTrue();
        OverrideBackend.IsGdsFactory(OverrideBackend.GdsFactory).ShouldBeTrue();
    }
}

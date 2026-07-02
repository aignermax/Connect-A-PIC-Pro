using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;
using static UnitTests.TestComponentFactory;

namespace UnitTests.ComponentSettings.InstanceOverride;

/// <summary>
/// Deterministic unit tests for <see cref="InstanceNazcaCodeEditorViewModel"/> (issue #556).
/// The preview service is mocked so no Python / Nazca is required.
/// </summary>
public class InstanceNazcaCodeEditorViewModelTests
{
    private const string TemplateCode = "def component():\n    return pdk.strt()\n";
    private const string RealSource = "def mmi2x2_dp():\n    with nd.Cell() as C:\n        pass\n    return C\n";
    private const string SourceNote = "# Source unavailable for this fixed-cell GDS component.\n";

    private static Mock<NazcaComponentPreviewService> MockService()
        => new(MockBehavior.Loose,
            "python3", "preview.py", (TimeSpan?)TimeSpan.FromSeconds(5), (ProcessLaunchFactory?)null) { CallBase = false };

    private static NazcaPreviewResult OkResult(double w = 12, double h = 6, string? source = null) => new()
    {
        Success = true,
        XMin = 0, YMin = 0, XMax = w, YMax = h,
        Source = source,
        Pins = new List<NazcaPreviewPin>()
    };

    /// <summary>Sets up module-mode RenderAsync (used by InitializeAsync / Reset).</summary>
    private static void SetupModuleRender(Mock<NazcaComponentPreviewService> mock, NazcaPreviewResult result)
        => mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static InstanceNazcaCodeEditorViewModel BuildVm(
        NazcaComponentPreviewService service,
        Dictionary<string, NazcaCodeOverride>? store = null,
        Component? live = null,
        Func<double, double, IReadOnlyList<string>>? overlapCheck = null,
        Action? onChanged = null)
    {
        return new InstanceNazcaCodeEditorViewModel(
            componentKey: "comp-1",
            storedOverrides: store ?? new Dictionary<string, NazcaCodeOverride>(),
            liveComponent: live,
            moduleName: "demo",
            nazcaFunction: "mmi2x2_dp",
            nazcaParameters: null,
            templateCode: TemplateCode,
            previewService: service,
            overlapCheck: overlapCheck,
            onDimensionsChanged: null,
            onChanged: onChanged);
    }

    [Fact]
    public void Constructor_SeedsCodeWithTemplate()
    {
        var vm = BuildVm(MockService().Object);
        vm.Code.ShouldBe(TemplateCode);
        vm.HasOverride.ShouldBeFalse();
        vm.IsValid.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------
    // InitializeAsync — real source / note / failure / stored-override paths.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_WithRealSource_ShowsSourceReadOnlyAndRenders()
    {
        var mock = MockService();
        SetupModuleRender(mock, OkResult(w: 20, h: 8, source: RealSource));
        var vm = BuildVm(mock.Object);

        await vm.InitializeAsync();

        vm.OriginalSource.ShouldBe(RealSource);   // shown read-only for reference
        vm.Code.ShouldNotBe(RealSource);          // editable box is the override starter
        vm.HasEditableSource.ShouldBeTrue();
        vm.PreviewData.ShouldNotBeNull();
        vm.StatusText.ShouldContain("Loaded");
    }

    [Fact]
    public async Task Initialize_WithSourceNote_MarksNotEditable_StillRenders()
    {
        var mock = MockService();
        SetupModuleRender(mock, OkResult(w: 14, h: 5, source: SourceNote));
        var vm = BuildVm(mock.Object);

        await vm.InitializeAsync();

        vm.OriginalSource.ShouldBe(SourceNote);
        vm.HasEditableSource.ShouldBeFalse();
        vm.PreviewData.ShouldNotBeNull();   // geometry still rendered
    }

    [Fact]
    public async Task Initialize_RenderFails_NotEditableNoSourceAndError()
    {
        var mock = MockService();
        SetupModuleRender(mock, NazcaPreviewResult.Fail("no nazca"));
        var vm = BuildVm(mock.Object);

        await vm.InitializeAsync();

        vm.HasEditableSource.ShouldBeFalse();
        vm.PreviewData.ShouldBeNull();
        vm.PreviewError.ShouldContain("no nazca");
        vm.OriginalSource.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Initialize_WhenOverrideStored_KeepsStoredCodeAndDoesNotRender()
    {
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride { RawCode = "def component():\n    return pdk.mine()\n" }
        };
        var mock = MockService();
        SetupModuleRender(mock, OkResult(source: RealSource));
        var vm = BuildVm(mock.Object, store);

        await vm.InitializeAsync();

        vm.Code.ShouldBe("def component():\n    return pdk.mine()\n");
        vm.HasEditableSource.ShouldBeTrue();
        mock.Verify(s => s.RenderAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Initialize_ServiceThrows_DoesNotEscapeAndMarksNotEditable()
    {
        var mock = MockService();
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var vm = BuildVm(mock.Object);

        await vm.InitializeAsync();   // must not throw

        vm.HasEditableSource.ShouldBeFalse();
        vm.PreviewError.ShouldContain("boom");
    }

    // ---------------------------------------------------------------------------
    // RunPreview (raw-code) — unchanged behaviour.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunPreview_Success_SetsIsValidAndClearsError()
    {
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult());
        var vm = BuildVm(mock.Object);

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeTrue();
        vm.PreviewError.ShouldBeEmpty();
        vm.PreviewData.ShouldNotBeNull();
        vm.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task RunPreview_Failure_SetsErrorAndNotValid()
    {
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("SyntaxError: bad code"));
        var vm = BuildVm(mock.Object);

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeFalse();
        vm.PreviewError.ShouldContain("SyntaxError");
        vm.PreviewData.ShouldBeNull();
    }

    [Fact]
    public async Task RunPreview_ServiceThrows_DoesNotEscapeAndMarksInvalid()
    {
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var vm = BuildVm(mock.Object);

        // Must not throw — the command swallows it.
        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.IsValid.ShouldBeFalse();
        vm.PreviewError.ShouldContain("boom");
    }

    [Fact]
    public void Apply_BeforeSuccessfulRun_IsDisabled()
    {
        var vm = BuildVm(MockService().Object);
        vm.ApplyOverrideCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_AfterRun_PersistsRawCodeAndRecomputesSize()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateBasicComponent();
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult(w: 33, h: 9));
        bool changed = false;
        var vm = BuildVm(mock.Object, store, live, onChanged: () => changed = true);
        vm.Code = "def component():\n    return pdk.custom()\n";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.CanExecute(null).ShouldBeTrue();
        vm.ApplyOverrideCommand.Execute(null);

        store.ShouldContainKey("comp-1");
        store["comp-1"].RawCode.ShouldBe("def component():\n    return pdk.custom()\n");
        store["comp-1"].OverrideWidthMicrometers!.Value.ShouldBe(33, tolerance: 0.001);
        store["comp-1"].OverrideHeightMicrometers!.Value.ShouldBe(9, tolerance: 0.001);
        live.WidthMicrometers.ShouldBe(33, tolerance: 0.001);
        live.HeightMicrometers.ShouldBe(9, tolerance: 0.001);
        vm.HasOverride.ShouldBeTrue();
        changed.ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_WhenNeighboursCollide_SetsOverlapFlagButStillApplies()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateBasicComponent();
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResult(w: 500, h: 500));
        var vm = BuildVm(mock.Object, store, live,
            overlapCheck: (_, _) => new[] { "Neighbour A" });

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        vm.HasOverlap.ShouldBeTrue();
        vm.StatusText.ShouldContain("Neighbour A");
        store["comp-1"].RawCode.ShouldNotBeNull();   // applied anyway (non-blocking)
        live.WidthMicrometers.ShouldBe(500, tolerance: 0.001);
    }

    // ---------------------------------------------------------------------------
    // Reset — now re-fetches the ORIGINAL source via module-mode RenderAsync.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Reset_ClearsCodeOverrideAndRestoresOriginalSource()
    {
        // Record also carries a parameter override, so Reset clears the #556
        // fields but keeps the record (the param override is unaffected).
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride
            {
                FunctionName = "custom_fn",
                RawCode = "old code",
                OverrideWidthMicrometers = 50,
                OverrideHeightMicrometers = 10
            }
        };
        var mock = MockService();
        SetupModuleRender(mock, OkResult(source: RealSource));
        var vm = BuildVm(mock.Object, store);

        // Seeded from stored override
        vm.Code.ShouldBe("old code");
        vm.HasOverride.ShouldBeTrue();

        await vm.ResetToTemplateCommand.ExecuteAsync(null);

        vm.OriginalSource.ShouldBe(RealSource);     // original source restored (read-only)
        vm.Code.ShouldNotBe("old code");            // editor reset to the override starter
        vm.HasEditableSource.ShouldBeTrue();
        vm.HasOverride.ShouldBeFalse();
        vm.IsValid.ShouldBeFalse();
        store["comp-1"].RawCode.ShouldBeNull();
        store["comp-1"].OverrideWidthMicrometers.ShouldBeNull();
        store["comp-1"].FunctionName.ShouldBe("custom_fn");  // param override preserved
    }

    [Fact]
    public async Task Reset_DropsRecordWhenNoParameterOverrideRemains()
    {
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride { RawCode = "x" }
        };
        var mock = MockService();
        SetupModuleRender(mock, OkResult(source: RealSource));
        var vm = BuildVm(mock.Object, store);

        await vm.ResetToTemplateCommand.ExecuteAsync(null);

        store.ShouldNotContainKey("comp-1");
    }
}

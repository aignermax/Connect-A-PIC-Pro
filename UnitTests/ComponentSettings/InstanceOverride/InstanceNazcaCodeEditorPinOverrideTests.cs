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
/// Tests for per-instance Nazca override pin re-derivation (issue #561):
/// Apply updates PhysicalPins, stores OverridePins/TemplatePins, sets
/// HasNoSimulationModel; Reset restores template pins.
/// </summary>
public class InstanceNazcaCodeEditorPinOverrideTests
{
    private const string TemplateCode = "def component():\n    return pdk.strt()\n";

    private static Mock<NazcaComponentPreviewService> MockService()
        => new(MockBehavior.Loose,
            "python3", "preview.py", (TimeSpan?)TimeSpan.FromSeconds(5), (ProcessLaunchFactory?)null) { CallBase = false };

    private static NazcaPreviewResult OkResultWithPins(
        double w = 20, double h = 10,
        IReadOnlyList<NazcaPreviewPin>? pins = null) => new()
    {
        Success = true,
        XMin = 0, YMin = 0, XMax = w, YMax = h,
        Pins = pins ?? Array.Empty<NazcaPreviewPin>(),
    };

    private static InstanceNazcaCodeEditorViewModel BuildVm(
        NazcaComponentPreviewService service,
        Dictionary<string, NazcaCodeOverride>? store = null,
        Component? live = null,
        Action<IReadOnlyList<PhysicalPin>>? onPinsChanged = null)
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
            overlapCheck: null,
            onDimensionsChanged: null,
            onChanged: null,
            onPinsChanged: onPinsChanged);
    }

    // ─── Apply updates live component PhysicalPins ────────────────────────────

    [Fact]
    public async Task Apply_WithPreviewPins_UpdatesLiveComponentPhysicalPins()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateBasicComponent();
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(w: 20, h: 10, pins: new[]
            {
                new NazcaPreviewPin { Name = "a0", X = 0, Y = 5, Angle = 180 },
                new NazcaPreviewPin { Name = "b0", X = 20, Y = 5, Angle = 0 },
            }));
        var vm = BuildVm(mock.Object, store, live);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        live.PhysicalPins.Count.ShouldBe(2);
        live.PhysicalPins[0].Name.ShouldBe("a0");
        live.PhysicalPins[1].Name.ShouldBe("b0");
        // Verify Y-flip: OffsetY = YMax − pinY = 10 − 5 = 5
        live.PhysicalPins[0].OffsetYMicrometers.ShouldBe(5, tolerance: 0.001);
        // Verify parent reference is set
        live.PhysicalPins[0].ParentComponent.ShouldBe(live);
    }

    [Fact]
    public async Task Apply_StoresOverridePinsInStore()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateBasicComponent();
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(w: 10, h: 5, pins: new[]
            {
                new NazcaPreviewPin { Name = "in", X = 0, Y = 2.5, Angle = 180 },
            }));
        var vm = BuildVm(mock.Object, store, live);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        store["comp-1"].OverridePins.ShouldNotBeNull();
        store["comp-1"].OverridePins!.Count.ShouldBe(1);
        store["comp-1"].OverridePins![0].Name.ShouldBe("in");
    }

    [Fact]
    public async Task Apply_CapturesTemplatePinsOnFirstApply()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateStraightWaveGuideWithPhysicalPins();  // has "in" and "out"
        live.PhysicalX = 0;
        live.PhysicalY = 0;
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(w: 20, h: 10, pins: new[]
            {
                new NazcaPreviewPin { Name = "a0", X = 0, Y = 5, Angle = 180 },
            }));
        var vm = BuildVm(mock.Object, store, live);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        // Template pins were "in" and "out" — captured before override applied.
        store["comp-1"].TemplatePins.ShouldNotBeNull();
        store["comp-1"].TemplatePins!.Select(p => p.Name).ShouldContain("in");
        store["comp-1"].TemplatePins!.Select(p => p.Name).ShouldContain("out");
    }

    [Fact]
    public async Task Apply_DifferentPinNames_SetsHasNoSimulationModel()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateStraightWaveGuideWithPhysicalPins();  // has "in" and "out"
        live.PhysicalX = 0;
        live.PhysicalY = 0;
        var mock = MockService();
        // Override has different pin names: "a0", "b0" instead of "in", "out"
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(w: 20, h: 10, pins: new[]
            {
                new NazcaPreviewPin { Name = "a0", X = 0, Y = 5, Angle = 180 },
                new NazcaPreviewPin { Name = "b0", X = 20, Y = 5, Angle = 0 },
            }));
        var vm = BuildVm(mock.Object, store, live);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        vm.HasNoSimulationModel.ShouldBeTrue();
        store["comp-1"].HasNoSimulationModel.ShouldBeTrue();
        vm.StatusText.ShouldContain("No simulation model");
    }

    [Fact]
    public async Task Apply_SamePinNames_DoesNotSetHasNoSimulationModel()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateStraightWaveGuideWithPhysicalPins();  // has "in" and "out"
        live.PhysicalX = 0;
        live.PhysicalY = 0;
        var mock = MockService();
        // Override keeps same pin names "in" and "out"
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(w: 20, h: 10, pins: new[]
            {
                new NazcaPreviewPin { Name = "in", X = 0, Y = 5, Angle = 180 },
                new NazcaPreviewPin { Name = "out", X = 20, Y = 5, Angle = 0 },
            }));
        var vm = BuildVm(mock.Object, store, live);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        vm.HasNoSimulationModel.ShouldBeFalse();
        store["comp-1"].HasNoSimulationModel.ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_FiresOnPinsChangedCallback()
    {
        var store = new Dictionary<string, NazcaCodeOverride>();
        var live = CreateBasicComponent();
        var mock = MockService();
        mock.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OkResultWithPins(pins: new[]
            {
                new NazcaPreviewPin { Name = "a0", X = 0, Y = 5, Angle = 180 },
            }));
        IReadOnlyList<PhysicalPin>? receivedPins = null;
        var vm = BuildVm(mock.Object, store, live,
            onPinsChanged: pins => receivedPins = pins);
        vm.Code = "def component(): pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.ApplyOverrideCommand.Execute(null);

        receivedPins.ShouldNotBeNull();
        receivedPins!.Count.ShouldBe(1);
    }

    // ─── Reset restores template pins ─────────────────────────────────────────

    [Fact]
    public async Task Reset_WithTemplatePins_RestoresLiveComponentPins()
    {
        // Start with a stored override that already has TemplatePins.
        var templatePins = new List<OverridePinData>
        {
            new() { Name = "in", OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180 },
            new() { Name = "out", OffsetXMicrometers = 20, OffsetYMicrometers = 5, AngleDegrees = 0 },
        };
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride
            {
                RawCode = "def component(): pass",
                TemplatePins = templatePins,
                OverridePins = new List<OverridePinData>
                {
                    new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180 },
                },
                HasNoSimulationModel = true,
            }
        };
        var live = CreateBasicComponent();
        // Current pins: the override pins (a0)
        live.PhysicalPins.Add(new PhysicalPin
            { Name = "a0", ParentComponent = live, OffsetXMicrometers = 0, OffsetYMicrometers = 5 });

        var mock = MockService();
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NazcaPreviewResult { Success = true, XMin = 0, XMax = 20, YMin = 0, YMax = 10 });

        var vm = BuildVm(mock.Object, store, live);

        await vm.ResetToTemplateCommand.ExecuteAsync(null);

        live.PhysicalPins.Count.ShouldBe(2);
        live.PhysicalPins[0].Name.ShouldBe("in");
        live.PhysicalPins[1].Name.ShouldBe("out");
        vm.HasNoSimulationModel.ShouldBeFalse();
        store.ShouldNotContainKey("comp-1");  // no param-override fields → record dropped
    }

    [Fact]
    public async Task Reset_ClearsOverridePinsAndNoSimModelFlag()
    {
        var store = new Dictionary<string, NazcaCodeOverride>
        {
            ["comp-1"] = new NazcaCodeOverride
            {
                RawCode = "def component(): pass",
                OverridePins = new List<OverridePinData> { new() { Name = "a0" } },
                HasNoSimulationModel = true,
            }
        };
        var mock = MockService();
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NazcaPreviewResult { Success = true, XMin = 0, XMax = 10, YMin = 0, YMax = 5 });

        var vm = BuildVm(mock.Object, store);

        await vm.ResetToTemplateCommand.ExecuteAsync(null);

        // Record dropped (no other override fields).
        store.ShouldNotContainKey("comp-1");
        vm.HasNoSimulationModel.ShouldBeFalse();
    }
}

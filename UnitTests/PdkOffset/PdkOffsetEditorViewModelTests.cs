using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using System.IO;
using System.Threading;

namespace UnitTests.PdkOffset;

/// <summary>
/// Unit and integration tests for the PDK Component Offset Editor ViewModel.
/// Verifies component loading, status badges, offset editing, pin deltas, and save-back.
/// </summary>
public class PdkOffsetEditorViewModelTests
{
    [Fact]
    public void BuildPreviewSource_GdsFactoryNativeDraft_EmitsFactoryCode_NotDemoCall()
    {
        // Before the fix, a gdsfactory-native draft (empty NazcaFunction) resolved to demo.(),
        // whose Nazca render crashed the offset editor. It must now emit real gdsfactory code (#570).
        var draft = new PdkComponentDraft
        {
            Name = "SiN MMI",
            NazcaFunction = "",
            GdsFactoryFunction = "cspdk.sin300.mmi1x2",
        };

        var source = PdkOffsetEditorViewModel.BuildPreviewSource(draft);

        source.ShouldContain("import cspdk.sin300");
        source.ShouldContain("component = gf.get_component('mmi1x2')");
        source.ShouldNotContain("demo.()");   // the broken empty-function Nazca call
    }

    [Fact]
    public async Task RenderForBatch_GdsFactoryNativeDraft_RoutesToGdsFactoryService_NotNazca()
    {
        // Check-All / Try-Fix-All render via RenderForBatch. It must use the same gdsfactory
        // routing as the interactive path — otherwise every gdsfactory component reports
        // RenderFailed ("module 'nazca.demofab' has no attribute ''") in the batch (#570).
        var nazca = new Mock<NazcaComponentPreviewService>(
            "py", "nazca.py", (System.TimeSpan?)null, (ProcessLaunchFactory?)null);
        var gf = new Mock<NazcaComponentPreviewService>(
            "py", "gf.py", (System.TimeSpan?)null, (ProcessLaunchFactory?)null);
        gf.Setup(s => s.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NazcaPreviewResult { Success = true });

        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), new PdkManagerViewModel(), nazca.Object, gf.Object);
        var draft = new PdkComponentDraft
        {
            Name = "SiN MMI",
            NazcaFunction = "",
            GdsFactoryFunction = "cspdk.sin300.mmi1x2",
        };

        var result = await vm.RenderForBatch(draft, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Success.ShouldBeTrue();
        gf.Verify(s => s.RenderRawCodeAsync(
            It.Is<string>(c => c.Contains("gf.get_component('mmi1x2')")), It.IsAny<CancellationToken>()), Times.Once);
        nazca.Verify(s => s.RenderAsync(
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PdkDraft BuildTestPdk(string pdkName = "Test PDK") => new()
    {
        Name = pdkName,
        Components = new List<PdkComponentDraft>
        {
            new()
            {
                Name = "Coupler",
                Category = "Couplers",
                NazcaFunction = "pdk.coupler",
                WidthMicrometers = 40,
                HeightMicrometers = 20,
                NazcaOriginOffsetX = 5.0,
                NazcaOriginOffsetY = 10.0,
                Pins = new List<PhysicalPinDraft>
                {
                    new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 5 },
                    new() { Name = "b0", OffsetXMicrometers = 40, OffsetYMicrometers = 5 }
                }
            },
            new()
            {
                Name = "Waveguide",
                Category = "Waveguides",
                NazcaFunction = "pdk.waveguide",
                WidthMicrometers = 100,
                HeightMicrometers = 5,
                NazcaOriginOffsetX = null,   // Missing!
                NazcaOriginOffsetY = null,
                Pins = new List<PhysicalPinDraft>
                {
                    new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 2.5 },
                    new() { Name = "b0", OffsetXMicrometers = 100, OffsetYMicrometers = 2.5 }
                }
            },
            new()
            {
                Name = "Splitter",
                Category = "Splitters",
                NazcaFunction = "pdk.splitter",
                WidthMicrometers = 30,
                HeightMicrometers = 15,
                NazcaOriginOffsetX = 0.0,   // Zero offset
                NazcaOriginOffsetY = 0.0,
                Pins = new List<PhysicalPinDraft>
                {
                    new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 7.5 }
                }
            }
        }
    };

    // ─── PdkComponentOffsetItemViewModel ───────────────────────────────────────

    [Fact]
    public void OffsetItem_WhenOffsetNull_HasMissingStatus()
    {
        var draft = BuildTestPdk().Components[1]; // Waveguide — null offsets
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.Missing);
        vm.StatusBadge.ShouldBe("❌");
    }

    [Fact]
    public void OffsetItem_WhenBothOffsetsZero_HasZeroOffsetStatus()
    {
        var draft = BuildTestPdk().Components[2]; // Splitter — zero offsets
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.ZeroOffset);
        vm.StatusBadge.ShouldBe("⚠️");
    }

    [Fact]
    public void OffsetItem_WhenOffsetNonZero_HasSetStatus()
    {
        var draft = BuildTestPdk().Components[0]; // Coupler — 5/10
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.Set);
        vm.StatusBadge.ShouldBe("✅");
    }

    [Fact]
    public void OffsetItem_RefreshStatus_UpdatesAfterDraftChange()
    {
        var draft = BuildTestPdk().Components[1]; // starts Missing
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");
        vm.Status.ShouldBe(OffsetStatus.Missing);

        draft.NazcaOriginOffsetX = 3.5;
        draft.NazcaOriginOffsetY = 7.0;
        vm.RefreshStatus();

        vm.Status.ShouldBe(OffsetStatus.Set);
    }

    [Fact]
    public void OffsetItem_WhenOnlyOneFieldNull_HasMissingStatus()
    {
        // Guards against a `&&`-vs-`||` typo in RefreshStatus. One-null must be
        // treated as Missing (can't GDS-export partial coordinates).
        var draft = BuildTestPdk().Components[0];
        draft.NazcaOriginOffsetX = 5.0;
        draft.NazcaOriginOffsetY = null;
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.Missing);
    }

    [Fact]
    public void OffsetItem_WhenFloatNoiseNearZero_StaysZeroOffset()
    {
        // Exact `== 0.0` comparison used to flip Status from ZeroOffset to Set
        // on any floating-point noise (GUI round-trip, serializer fuzz).
        var draft = BuildTestPdk().Components[2];
        draft.NazcaOriginOffsetX = 1e-18;
        draft.NazcaOriginOffsetY = -2e-18;
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.ZeroOffset);
    }

    [Fact]
    public void OffsetItem_WhenOneFieldZeroOneNonzero_IsSet()
    {
        // Documents the boundary between ZeroOffset and Set: "both near-zero"
        // is ZeroOffset, "any one non-zero" is Set.
        var draft = BuildTestPdk().Components[0];
        draft.NazcaOriginOffsetX = 0.0;
        draft.NazcaOriginOffsetY = 10.0;
        var vm = new PdkComponentOffsetItemViewModel(draft, "Test PDK");

        vm.Status.ShouldBe(OffsetStatus.Set);
    }

    // ─── PinPositionViewModel ──────────────────────────────────────────────────

    [Fact]
    public void PinPosition_NazcaRelX_IsLocalXMinusOffsetX()
    {
        var pin = new PinPositionViewModel("a0", localX: 0, localY: 5,
                                           nazcaOffsetX: 5, nazcaOffsetY: 10);

        pin.NazcaRelX.ShouldBe(-5.0);  // 0 - 5
    }

    [Fact]
    public void PinPosition_NazcaRelY_IsOffsetYMinusLocalY()
    {
        // Mapper convention: NazcaOriginOffsetY is the org's distance below
        // the bbox top edge, so NazcaRelY = offsetY - localY.
        var pin = new PinPositionViewModel("a0", localX: 0, localY: 5,
                                           nazcaOffsetX: 5, nazcaOffsetY: 10);

        pin.NazcaRelY.ShouldBe(5.0);   // 10 - 5 = 5
    }

    [Fact]
    public void PinPosition_WhenZeroOffset_NazcaRelXEqualsLocalX()
    {
        var pin = new PinPositionViewModel("a0", localX: 15.0, localY: 5,
                                           nazcaOffsetX: 0, nazcaOffsetY: 0);

        pin.NazcaRelX.ShouldBe(15.0);
    }

    [Fact]
    public void PinPosition_WithNegativeOffsetAndPinAtOrigin_ComputesCorrectly()
    {
        // Covers the negative-offset arithmetic path that the positive-only
        // tests above skip. Pin at (0, 0), offset (-3, -4):
        //   NazcaRelX = 0 - (-3) = 3
        //   NazcaRelY = -4 - 0 = -4
        var pin = new PinPositionViewModel("a0", localX: 0, localY: 0,
                                           nazcaOffsetX: -3, nazcaOffsetY: -4);

        pin.NazcaRelX.ShouldBe(3.0);
        pin.NazcaRelY.ShouldBe(-4.0);
    }

    // ─── PdkOffsetEditorViewModel ──────────────────────────────────────────────

    private static PdkOffsetEditorViewModel CreateViewModel()
    {
        return new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), new PdkManagerViewModel());
    }

    [Fact]
    public void ViewModel_InitialState_HasEmptyComponentsAndReadyStatus()
    {
        var vm = CreateViewModel();

        vm.Components.ShouldBeEmpty();
        vm.SelectedComponent.ShouldBeNull();
        vm.PinPositions.ShouldBeEmpty();
    }

    [Fact]
    public void ViewModel_SelectComponent_PopulatesPinPositions()
    {
        var vm = CreateViewModel();
        var pdk = BuildTestPdk();
        // Simulate what LoadPdkFile does
        foreach (var comp in pdk.Components)
            vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));

        vm.SelectedComponent = vm.Components[0]; // Coupler with 2 pins

        vm.PinPositions.Count.ShouldBe(2);
        vm.PinPositions[0].PinName.ShouldBe("a0");
        vm.PinPositions[1].PinName.ShouldBe("b0");
    }

    [Fact]
    public void ViewModel_ApplyOffset_UpdatesDraftAndRefreshesPins()
    {
        var vm = CreateViewModel();
        var pdk = BuildTestPdk();
        foreach (var comp in pdk.Components)
            vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));

        vm.SelectedComponent = vm.Components[1]; // Waveguide — was missing

        vm.OffsetX = 2.0;
        vm.OffsetY = 4.0;
        vm.ApplyOffsetCommand.Execute(null);

        vm.SelectedComponent.Draft.NazcaOriginOffsetX.ShouldBe(2.0);
        vm.SelectedComponent.Draft.NazcaOriginOffsetY.ShouldBe(4.0);
        vm.SelectedComponent.Status.ShouldBe(OffsetStatus.Set);
        vm.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public void ViewModel_ApplyOffset_WithNoSelection_IsNoOpWithStatus()
    {
        var vm = CreateViewModel();
        vm.OffsetX = 5.0;
        vm.OffsetY = 2.0;

        vm.ApplyOffsetCommand.Execute(null);

        vm.HasUnsavedChanges.ShouldBeFalse();
        vm.StatusText.ShouldContain("Select a component");
    }

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(0.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(0.0, double.NegativeInfinity)]
    public void ViewModel_ApplyOffset_RejectsNonFiniteValues(double badX, double badY)
    {
        var vm = CreateViewModel();
        var pdk = BuildTestPdk();
        foreach (var comp in pdk.Components)
            vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));

        vm.SelectedComponent = vm.Components[1]; // Waveguide — Missing
        vm.OffsetX = badX;
        vm.OffsetY = badY;

        vm.ApplyOffsetCommand.Execute(null);

        // Draft stays untouched — no silent propagation of NaN into the JSON.
        vm.SelectedComponent.Draft.NazcaOriginOffsetX.ShouldBeNull();
        vm.SelectedComponent.Draft.NazcaOriginOffsetY.ShouldBeNull();
        vm.HasUnsavedChanges.ShouldBeFalse();
        vm.StatusText.ShouldContain("finite");
    }

    [Fact]
    public void ViewModel_SelectingMissingComponent_ShowsWarningInStatus()
    {
        // The edit fields default to 0/0 for a null-offset component. A user
        // who clicks Apply without reading the warning would silently convert
        // "no offset in JSON" into "offset = 0". Make the state visible.
        var vm = CreateViewModel();
        var pdk = BuildTestPdk();
        foreach (var comp in pdk.Components)
            vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));

        vm.SelectedComponent = vm.Components[1]; // Waveguide — Missing

        vm.StatusText.ShouldContain("no offset in the JSON");
    }

    [Fact]
    public void ViewModel_ApplyOffset_RefreshesPinNazcaCoordinates()
    {
        var vm = CreateViewModel();
        var pdk = BuildTestPdk();
        foreach (var comp in pdk.Components)
            vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));

        vm.SelectedComponent = vm.Components[0]; // Coupler: offset (5, 10), height 20
        vm.OffsetX = 0.0;
        vm.OffsetY = 0.0;
        vm.ApplyOffsetCommand.Execute(null);

        // Pin a0: localX=0, localY=5, offset=(0,0)
        // NazcaRelX = 0 - 0 = 0
        // NazcaRelY = 0 - 5 = -5 (mapper convention: offsetY - localY)
        vm.PinPositions[0].NazcaRelX.ShouldBe(0.0);
        vm.PinPositions[0].NazcaRelY.ShouldBe(-5.0);
    }

    [Fact]
    public void ViewModel_SavePdk_WritesJsonAndClearsUnsavedChanges()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write a minimal valid PDK JSON that PdkLoader can read
            var pdk = BuildTestPdk();
            new PdkJsonSaver().SaveToFile(pdk, tempFile);

            var vm = CreateViewModel();
            // Manually simulate loaded state
            foreach (var comp in pdk.Components)
                vm.Components.Add(new PdkComponentOffsetItemViewModel(comp, pdk.Name));
            // Use reflection to set private fields for save path
            SetPrivateField(vm, "_loadedPdk", pdk);
            SetPrivateField(vm, "_loadedFilePath", tempFile);

            vm.SelectedComponent = vm.Components[1];
            vm.OffsetX = 3.0;
            vm.OffsetY = 6.0;
            vm.ApplyOffsetCommand.Execute(null);

            vm.SavePdkCommand.Execute(null);

            vm.HasUnsavedChanges.ShouldBeFalse();
            vm.StatusText.ShouldContain(Path.GetFileName(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── PdkJsonSaver ──────────────────────────────────────────────────────────

    [Fact]
    public void PdkJsonSaver_SaveAndReload_PreservesOffsets()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var pdk = BuildTestPdk();
            pdk.Components[0].NazcaOriginOffsetX = 7.5;
            pdk.Components[0].NazcaOriginOffsetY = 3.25;

            var saver = new PdkJsonSaver();
            saver.SaveToFile(pdk, tempFile);

            var loader = new PdkLoader();
            // PdkLoader.LoadFromFile validates — we need a valid file;
            // skip the validator by reading raw JSON
            var json = File.ReadAllText(tempFile);
            json.ShouldContain("7.5");
            json.ShouldContain("3.25");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void PdkJsonSaver_PreservesFloatPrecisionInJson()
    {
        // The existing round-trip test above only greps for "3.25". Protects
        // against JsonSerializerOptions regressions on culture / precision.
        // Deliberately checks values that would truncate with a naive F-format:
        // 0.1+0.2 → 0.30000000000000004, and a value that needs ≥ 7 sig digits.
        var tempFile = Path.GetTempFileName();
        try
        {
            var pdk = BuildTestPdk();
            pdk.Components[0].NazcaOriginOffsetX = 0.1 + 0.2;
            pdk.Components[0].NazcaOriginOffsetY = 1234.5678901;

            new PdkJsonSaver().SaveToFile(pdk, tempFile);
            var json = File.ReadAllText(tempFile);

            json.ShouldContain("0.30000000000000004");
            json.ShouldContain("1234.5678901");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void PdkJsonSaver_WhenAllOffsetsNull_OmitsOffsetProperties()
    {
        // `DefaultIgnoreCondition.WhenWritingNull` on the saver's options is a
        // contract: un-calibrated components must not flip to "0.0" in JSON.
        // Otherwise the editor's missing-vs-zero-offset distinction collapses
        // after one save and the user can never tell which components were
        // actually calibrated.
        var tempFile = Path.GetTempFileName();
        try
        {
            var pdk = new PdkDraft
            {
                Name = "Null-offsets",
                Components = new List<PdkComponentDraft>
                {
                    new()
                    {
                        Name = "untouched",
                        WidthMicrometers = 10,
                        HeightMicrometers = 10,
                        NazcaOriginOffsetX = null,
                        NazcaOriginOffsetY = null,
                    }
                }
            };

            new PdkJsonSaver().SaveToFile(pdk, tempFile);
            var json = File.ReadAllText(tempFile);

            json.ShouldNotContain("nazcaOriginOffsetX");
            json.ShouldNotContain("nazcaOriginOffsetY");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(obj, value);
    }
}

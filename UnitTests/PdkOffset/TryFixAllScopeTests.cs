using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Round-5 review [8]: Try-Fix-All must only rewrite MISALIGNED components
/// (&gt; 0.5 µm). The CheckAlignment band (0.1–0.5 µm) means "human should check" —
/// deltas there can come from preview-render quantization, and bulk-rewriting
/// calibrations that earlier releases certified as aligned would silently shift
/// pins of every future placement without per-component consent.
/// </summary>
public class TryFixAllScopeTests : IDisposable
{
    private readonly string _pdkPath = Path.Combine(
        Path.GetTempPath(), "lunima-tryfixscope-" + Guid.NewGuid().ToString("N") + ".json");

    public TryFixAllScopeTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    public void Dispose()
    {
        if (File.Exists(_pdkPath)) File.Delete(_pdkPath);
    }

    /// <summary>
    /// PDK with a check-band component (0.3 µm delta against the mocked render) and a
    /// genuinely misaligned one (5.0 µm). Geometry mirrors the tolerance-tier tests:
    /// pin at (0, height), NazcaOriginOffsetY = height, render pin at the Nazca origin —
    /// the resulting delta equals NazcaOriginOffsetX.
    /// </summary>
    private const string PdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""FixScope PDK"",
        ""components"": [
            {
                ""name"": ""CheckBand"",
                ""category"": ""Test"",
                ""nazcaFunction"": ""pdk.checkband"",
                ""widthMicrometers"": 10,
                ""heightMicrometers"": 10,
                ""nazcaOriginOffsetX"": 0.3,
                ""nazcaOriginOffsetY"": 10,
                ""pins"": [ { ""name"": ""in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 10 } ]
            },
            {
                ""name"": ""Broken"",
                ""category"": ""Test"",
                ""nazcaFunction"": ""pdk.broken"",
                ""widthMicrometers"": 10,
                ""heightMicrometers"": 10,
                ""nazcaOriginOffsetX"": 5.0,
                ""nazcaOriginOffsetY"": 10,
                ""pins"": [ { ""name"": ""in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 10 } ]
            }
        ]
    }";

    [Fact]
    public async Task TryFixAll_RewritesMisalignedOnly_AndLeavesCheckBandUntouched()
    {
        File.WriteAllText(_pdkPath, PdkJson);
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("FixScope PDK", _pdkPath, isBundled: false, componentCount: 2);

        var render = new NazcaPreviewResult
        {
            Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 10,
            Pins = new List<NazcaPreviewPin> { new() { Name = "opt1", X = 0, Y = 0 } }
        };
        var nazca = new Mock<NazcaComponentPreviewService>(
            "py", "nazca.py", (TimeSpan?)null, (ProcessLaunchFactory?)null);
        nazca.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(render);

        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), manager, nazca.Object)
        {
            UiThreadMarshaller = action => { action(); return Task.CompletedTask; }
        };
        vm.SelectedInstalledPdk = vm.AvailablePdks[0];
        var checkBand = vm.Components.Single(c => c.ComponentName == "CheckBand").Draft;
        var broken = vm.Components.Single(c => c.ComponentName == "Broken").Draft;

        await vm.TryFixAllCommand.ExecuteAsync(null);

        // The check-band component keeps its shipped calibration…
        checkBand.NazcaOriginOffsetX.ShouldBe(0.3);
        checkBand.Pins[0].OffsetXMicrometers.ShouldBe(0);
        vm.BatchCheckResults.Single(r => r.ComponentName == "CheckBand")
            .Status.ShouldBe(ComponentCheckStatus.CheckAlignment,
                "check-band rows are reported, not silently rewritten");

        // …while the genuinely misaligned one is auto-calibrated.
        broken.NazcaOriginOffsetX.ShouldNotBe(5.0);
        vm.BatchCheckResults.Single(r => r.ComponentName == "Broken")
            .Status.ShouldBe(ComponentCheckStatus.Aligned);
    }
}

using CAP.Avalonia.ViewModels.PdkResolution;
using CAP_Core.Export.PdkResolution;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.PdkResolution;

/// <summary>
/// Tests the "Tools → Check PDKs against Python" ViewModel (issue #515) with
/// a mocked resolution service — no Python subprocess involved.
/// </summary>
public class PdkResolutionCheckViewModelTests : IDisposable
{
    private readonly string _pdkDir;

    public PdkResolutionCheckViewModelTests()
    {
        _pdkDir = Path.Combine(Path.GetTempPath(), $"lunima_pdkcheck_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_pdkDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pdkDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private void WritePdk(string fileName, params (string Name, string NazcaFunction)[] components)
    {
        var comps = string.Join(",", components.Select(c => $$"""
            {
                "name": "{{c.Name}}",
                "nazcaFunction": "{{c.NazcaFunction}}",
                "widthMicrometers": 100,
                "heightMicrometers": 50,
                "pins": [ { "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 25, "angleDegrees": 180 } ]
            }
            """));
        File.WriteAllText(Path.Combine(_pdkDir, fileName),
            $$"""{ "name": "Test PDK", "components": [ {{comps}} ] }""");
    }

    private static Mock<PdkFunctionResolutionService> MockService()
        => new("python3", "script.py", null, null);

    private PdkResolutionCheckViewModel CreateViewModel(PdkFunctionResolutionService service)
        => new(new PdkLoader(), service, () => _pdkDir);

    [Fact]
    public async Task RunCheck_DeadReference_AppearsAsError()
    {
        // The known failure from PR #511: demo_pdk.ring_resonator does not exist.
        WritePdk("demo-pdk.json", ("Ring Resonator", "demo_pdk.ring_resonator"));
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .ReturnsAsync(new PdkResolutionReport
            {
                Success = true,
                Results = new[]
                {
                    new PdkResolutionResult
                    {
                        Name = "Ring Resonator",
                        Status = PdkResolutionStatus.Error,
                        Message = "module 'nazca.demofab' has no attribute 'ring_resonator'"
                    }
                }
            });
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.Pdks.Count.ShouldBe(1);
        var row = vm.Pdks[0].Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe(PdkResolutionStatus.Error);
        row.StatusBadge.ShouldBe("❌");
        row.FunctionPath.ShouldBe("demo_pdk.ring_resonator");
        vm.HasFailures.ShouldBeTrue();
        vm.StatusText.ShouldContain("1 dead reference");
    }

    [Fact]
    public async Task RunCheck_MapsNazcaFunctionsToModulesBeforeCallingService()
    {
        WritePdk("mixed-pdk.json",
            ("Ring", "demo_pdk.ring_resonator"),
            ("Y-Branch", "ebeam_y_1550"));
        IReadOnlyList<PdkResolutionEntry>? captured = null;
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .Callback<IReadOnlyList<PdkResolutionEntry>, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(new PdkResolutionReport { Success = true });
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        captured.ShouldNotBeNull();
        captured.Count.ShouldBe(2);
        captured[0].Module.ShouldBe("demo");
        captured[0].Function.ShouldBe("ring_resonator");
        captured[1].Module.ShouldBe("siepic_ebeam_pdk");
        captured[1].Function.ShouldBe("ebeam_y_1550");
    }

    [Fact]
    public async Task RunCheck_GdsFactoryNativeComponent_ChecksGdsFactoryFunction()
    {
        // CornerStone-style component: nazcaFunction empty, gdsFactoryFunction set. The check
        // must verify the gdsfactory path instead of reporting "empty nazcaFunction" (#515 review).
        File.WriteAllText(Path.Combine(_pdkDir, "gf-pdk.json"),
            """
            { "name": "GF PDK", "components": [ {
                "name": "Coupler",
                "nazcaFunction": "",
                "gdsFactoryFunction": "cspdk.sin300.coupler",
                "widthMicrometers": 100, "heightMicrometers": 50,
                "pins": [ { "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 25, "angleDegrees": 180 } ]
            } ] }
            """);
        IReadOnlyList<PdkResolutionEntry>? captured = null;
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .Callback<IReadOnlyList<PdkResolutionEntry>, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(new PdkResolutionReport
            {
                Success = true,
                Results = new[] { new PdkResolutionResult { Name = "Coupler", Status = PdkResolutionStatus.Ok } }
            });
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        captured.ShouldNotBeNull();
        var entry = captured.ShouldHaveSingleItem();
        entry.Module.ShouldBe("cspdk.sin300");
        entry.Function.ShouldBe("coupler");
        var row = vm.Pdks[0].Rows.ShouldHaveSingleItem();
        row.FunctionPath.ShouldBe("cspdk.sin300.coupler");   // shows gdsFactoryFunction, not empty
    }

    [Fact]
    public async Task RunCheck_AnalyzerSentinel_IsSkipped()
    {
        WritePdk("tools-pdk.json", ("ONA Analyzer", "__analyzer__"));
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .ReturnsAsync(new PdkResolutionReport { Success = true });
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.Pdks[0].Rows.ShouldBeEmpty();
        service.Verify(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default),
            Times.Never);
    }

    [Fact]
    public async Task RunCheck_ServiceFailure_SurfacesAsGroupError()
    {
        WritePdk("demo-pdk.json", ("Straight", "demo.strt"));
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .ReturnsAsync(PdkResolutionReport.Fail("Could not start Python 'python3'"));
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.Pdks[0].HasError.ShouldBeTrue();
        vm.Pdks[0].Error.ShouldContain("Could not start Python");
        vm.HasFailures.ShouldBeTrue();
    }

    [Fact]
    public async Task RunCheck_MissingDirectory_ReportsNoPdkDirectory()
    {
        var vm = new PdkResolutionCheckViewModel(
            new PdkLoader(), MockService().Object, () => null);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.Pdks.ShouldBeEmpty();
        vm.StatusText.ShouldBe("No PDK directory found.");
    }

    [Fact]
    public async Task BuildFailingListText_ContainsOnlyFailingRows()
    {
        WritePdk("demo-pdk.json",
            ("Straight", "demo.strt"),
            ("Ring Resonator", "demo_pdk.ring_resonator"));
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .ReturnsAsync(new PdkResolutionReport
            {
                Success = true,
                Results = new[]
                {
                    new PdkResolutionResult { Name = "Straight", Status = PdkResolutionStatus.Ok, Message = "callable" },
                    new PdkResolutionResult
                    {
                        Name = "Ring Resonator",
                        Status = PdkResolutionStatus.Error,
                        Message = "module 'nazca.demofab' has no attribute 'ring_resonator'"
                    }
                }
            });
        var vm = CreateViewModel(service.Object);
        await vm.RunCheckCommand.ExecuteAsync(null);

        var text = vm.BuildFailingListText();

        text.ShouldContain("demo-pdk.json");
        text.ShouldContain("Ring Resonator");
        text.ShouldContain("demo_pdk.ring_resonator");
        text.ShouldNotContain("Straight");
    }

    [Fact]
    public async Task RunCheck_AllOk_NoFailuresAndEmptyFailingList()
    {
        WritePdk("demo-pdk.json", ("Straight", "demo.strt"));
        var service = MockService();
        service.Setup(s => s.ResolveAsync(It.IsAny<IReadOnlyList<PdkResolutionEntry>>(), default))
            .ReturnsAsync(new PdkResolutionReport
            {
                Success = true,
                Results = new[]
                {
                    new PdkResolutionResult { Name = "Straight", Status = PdkResolutionStatus.Ok, Message = "callable" }
                }
            });
        var vm = CreateViewModel(service.Object);

        await vm.RunCheckCommand.ExecuteAsync(null);

        vm.HasFailures.ShouldBeFalse();
        vm.BuildFailingListText().ShouldBeEmpty();
        vm.Pdks[0].Summary.ShouldBe("1 ok, 0 warnings, 0 errors");
    }
}

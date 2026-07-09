using CAP_Core.Export.PdkResolution;
using Shouldly;
using Xunit;

namespace UnitTests.Export.PdkResolution;

/// <summary>
/// Exercises the JSON parsing path of <see cref="PdkFunctionResolutionService"/>
/// without spawning a real Python subprocess (the CI Linux box may lack nazca).
/// </summary>
public class PdkFunctionResolutionServiceTests
{
    [Fact]
    public void ParseOutput_SuccessWithResults_ParsesAllEntries()
    {
        var stdout = """
            {"success": true, "results": [
                {"name": "Straight WG", "status": "ok", "kind": "callable", "message": "nazca.demofab.strt is callable"},
                {"name": "Ring Resonator", "status": "error", "kind": "", "message": "module 'nazca.demofab' has no attribute 'ring_resonator'"},
                {"name": "Odd One", "status": "warning", "kind": "attribute", "message": "exists but is not callable"}
            ]}
            """.Replace("\n", " ");

        var report = PdkFunctionResolutionService.ParseOutput(stdout);

        report.Success.ShouldBeTrue();
        report.Results.Count.ShouldBe(3);
        report.Results[0].Status.ShouldBe(PdkResolutionStatus.Ok);
        report.Results[0].Kind.ShouldBe("callable");
        report.Results[1].Name.ShouldBe("Ring Resonator");
        report.Results[1].Status.ShouldBe(PdkResolutionStatus.Error);
        report.Results[1].Message.ShouldContain("has no attribute 'ring_resonator'");
        report.Results[2].Status.ShouldBe(PdkResolutionStatus.Warning);
    }

    [Fact]
    public void ParseOutput_RunLevelFailure_ReturnsError()
    {
        var report = PdkFunctionResolutionService.ParseOutput(
            """{"success": false, "error": "cannot read input file"}""");

        report.Success.ShouldBeFalse();
        report.Error.ShouldBe("cannot read input file");
    }

    [Fact]
    public void ParseOutput_NazcaChatterBeforeJson_PicksTrailingJsonLine()
    {
        var stdout = "INFO   : pin2pin drc: True\nloaded demofab\n" +
                     """{"success": true, "results": [{"name": "X", "status": "ok", "kind": "callable", "message": "ok"}]}""";

        var report = PdkFunctionResolutionService.ParseOutput(stdout);

        report.Success.ShouldBeTrue();
        report.Results.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    public void ParseOutput_NoJson_ReturnsFailure(string stdout)
    {
        var report = PdkFunctionResolutionService.ParseOutput(stdout);

        report.Success.ShouldBeFalse();
        report.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ParseOutput_NoJsonButStderr_SurfacesPythonErrorExcerpt()
    {
        // #515 review fix: a run that prints a traceback to stderr and no JSON must surface the
        // Python error, not a bare "no JSON output". Also exercises LastLines (trims, keeps tail).
        var stderr = "Traceback (most recent call last):\n"
                   + "  File \"list_pdk_resolution.py\", line 1, in <module>\n"
                   + "ModuleNotFoundError: No module named 'nazca'\n";

        var report = PdkFunctionResolutionService.ParseOutput(stdout: "chatter but no json", stderr: stderr);

        report.Success.ShouldBeFalse();
        report.Error.ShouldContain("Python error");
        report.Error.ShouldContain("No module named 'nazca'");
    }

    [Fact]
    public void ParseOutput_UnknownStatus_MapsToError()
    {
        var report = PdkFunctionResolutionService.ParseOutput(
            """{"success": true, "results": [{"name": "X", "status": "bogus", "kind": "", "message": ""}]}""");

        report.Results[0].Status.ShouldBe(PdkResolutionStatus.Error);
    }

    [Fact]
    public void SerializeEntries_ProducesScriptInputShape()
    {
        var entries = new[]
        {
            new PdkResolutionEntry { Name = "Ring Resonator", Module = "demo", Function = "ring_resonator" }
        };

        var json = PdkFunctionResolutionService.SerializeEntries(entries);

        json.ShouldContain("\"name\":\"Ring Resonator\"");
        json.ShouldContain("\"module\":\"demo\"");
        json.ShouldContain("\"function\":\"ring_resonator\"");
    }

    [Fact]
    public async Task ResolveAsync_MissingScript_ReturnsFailureReport()
    {
        var service = new PdkFunctionResolutionService(
            "python3", "/nonexistent/list_pdk_resolution.py");

        var report = await service.ResolveAsync(
            new[] { new PdkResolutionEntry { Name = "X", Module = "demo", Function = "strt" } });

        report.Success.ShouldBeFalse();
        report.Error.ShouldContain("Resolution script not found");
    }

    [Fact]
    public async Task ResolveAsync_EmptyEntries_ReturnsSuccessWithoutRunningScript()
    {
        var service = new PdkFunctionResolutionService(
            "python3", "/nonexistent/list_pdk_resolution.py");

        var report = await service.ResolveAsync(Array.Empty<PdkResolutionEntry>());

        report.Success.ShouldBeTrue();
        report.Results.ShouldBeEmpty();
    }
}

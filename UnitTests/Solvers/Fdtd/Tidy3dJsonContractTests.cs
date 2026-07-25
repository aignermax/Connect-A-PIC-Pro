using System.Text.Json;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

public class Tidy3dJsonContractTests
{
    private static FdtdSMatrixRequest SampleRequest() => new()
    {
        GdsPath = "/tmp/component.gds",
        Ports = new[] { new FdtdPort { Name = "o1" }, new FdtdPort { Name = "o2" } },
        WavelengthStart = 1.5,
        WavelengthStop = 1.6,
        WavelengthPoints = 11,
    };

    [Fact]
    public void SerialiseRequest_InjectsModeAndKeepsHostGdsPath()
    {
        var json = Tidy3dJsonContract.SerialiseRequest(SampleRequest(), Tidy3dJsonContract.ModeSolve);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("mode").GetString().ShouldBe("solve");
        // Unlike the Docker bridge there is no container path mapping.
        root.GetProperty("gds_path").GetString().ShouldBe("/tmp/component.gds");
        root.GetProperty("wavelength_points").GetInt32().ShouldBe(11);
        root.GetProperty("ports").GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData(Tidy3dJsonContract.ModeCheck, "check")]
    [InlineData(Tidy3dJsonContract.ModeEstimate, "estimate")]
    public void SerialiseRequest_SupportsAllModes(string mode, string expected)
    {
        var json = Tidy3dJsonContract.SerialiseRequest(SampleRequest(), mode);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("mode").GetString().ShouldBe(expected);
    }

    [Fact]
    public void ParseCheck_Success_ReportsVersion()
    {
        var result = Tidy3dJsonContract.ParseCheck(
            "log chatter\n{\"success\": true, \"tidy3d_version\": \"2.7.0\"}");

        result.IsAvailable.ShouldBeTrue();
        result.Message.ShouldContain("2.7.0");
    }

    [Fact]
    public void ParseCheck_Failure_SurfacesErrorVerbatim()
    {
        var result = Tidy3dJsonContract.ParseCheck(
            "{\"success\": false, \"error\": \"tidy3d is not installed. Install with: pip install tidy3d\"}");

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("pip install tidy3d");
    }

    [Fact]
    public void ParseCheck_NoJson_ReportsFirstStderrLine()
    {
        var result = Tidy3dJsonContract.ParseCheck("", "Traceback: boom\nmore lines");

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("boom");
    }

    [Fact]
    public void ParseEstimate_Success_ReadsCreditsAndCount()
    {
        var result = Tidy3dJsonContract.ParseEstimate(
            "{\"success\": true, \"estimated_credits\": 1.25, \"simulation_count\": 2}");

        result.Success.ShouldBeTrue();
        result.EstimatedCredits.ShouldBe(1.25);
        result.SimulationCount.ShouldBe(2);
    }

    [Fact]
    public void ParseEstimate_Failure_SurfacesErrorVerbatim()
    {
        var result = Tidy3dJsonContract.ParseEstimate(
            "{\"success\": false, \"error\": \"API key rejected by server\"}");

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("API key rejected");
    }

    [Fact]
    public void ParseEstimate_NoJson_Fails()
    {
        var result = Tidy3dJsonContract.ParseEstimate("no json here");

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("no result");
    }

    [Fact]
    public void ParseCheck_OldTidy3dVersion_SurfacesUpgradeGuidance()
    {
        // scripts/tidy3d_sparams.py gates on tidy3d >= 2.10 (ModalComponentModeler).
        var result = Tidy3dJsonContract.ParseCheck(
            """{"success": false, "error": "tidy3d >= 2.10 required (found 2.7.0). Upgrade with: pip install -U tidy3d", "missing_backend": "tidy3d"}""");

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("2.10");
        result.Message.ShouldContain("pip install -U tidy3d");
    }

    [Fact]
    public void ParseCheck_MissingApiKeyFlag_MapsToReason()
    {
        var result = Tidy3dJsonContract.ParseCheck(
            """{"success": false, "error": "No Tidy3D API key configured.", "missing_backend": null, "missing_api_key": true}""");

        result.IsAvailable.ShouldBeFalse();
        result.Reason.ShouldBe(FdtdUnavailableReason.MissingApiKey);
    }

    [Fact]
    public void ParseCheck_WithoutMissingApiKeyFlag_KeepsReasonNone()
    {
        var result = Tidy3dJsonContract.ParseCheck(
            """{"success": false, "error": "tidy3d is not installed.", "missing_backend": "tidy3d"}""");

        result.IsAvailable.ShouldBeFalse();
        result.Reason.ShouldBe(FdtdUnavailableReason.None);
    }

    [Theory]
    // Wrong-typed values throw InvalidOperationException from GetBoolean()/GetString()
    // — the probe must degrade to unavailable, never crash.
    [InlineData("""{"success": "yes"}""")]
    [InlineData("""{"success": true, "tidy3d_version": 42}""")]
    [InlineData("""{"success": false, "error": 123}""")]
    public void ParseCheck_WrongTypedJson_FailsGracefully(string stdout)
    {
        var result = Tidy3dJsonContract.ParseCheck(stdout);

        result.IsAvailable.ShouldBeFalse();
        result.Message.ShouldContain("Could not parse");
    }

    [Theory]
    [InlineData("""{"success": "yes"}""")]
    [InlineData("""{"success": true, "estimated_credits": "a lot"}""")]
    [InlineData("""{"success": true, "estimated_credits": 1.2, "simulation_count": "two"}""")]
    public void ParseEstimate_WrongTypedJson_FailsGracefully(string stdout)
    {
        var result = Tidy3dJsonContract.ParseEstimate(stdout);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("Could not parse");
    }

    [Fact]
    public void ParseOutput_FailureWithTrace_AppendsTraceToDiagnostics()
    {
        const string json =
            """{"success": false, "error": "cloud run failed", "trace": "Traceback (most recent call last): boom"}""";

        var result = FdtdJsonContract.ParseOutput(json, stderr: "solver stderr");

        result.Success.ShouldBeFalse();
        result.RawStderr.ShouldContain("solver stderr");
        result.RawStderr.ShouldContain("Traceback (most recent call last): boom");
    }

    [Fact]
    public void ParseOutput_FailureWithTraceOnly_UsesTraceAsDiagnostics()
    {
        const string json = """{"success": false, "error": "boom", "trace": "File \"x.py\", line 1"}""";

        var result = FdtdJsonContract.ParseOutput(json);

        result.Success.ShouldBeFalse();
        result.RawStderr.ShouldContain("x.py");
    }

    [Fact]
    public void ParseOutput_FailureWithoutTrace_KeepsStderrUnchanged()
    {
        const string json = """{"success": false, "error": "boom"}""";

        var result = FdtdJsonContract.ParseOutput(json, stderr: "plain stderr");

        result.Success.ShouldBeFalse();
        result.RawStderr.ShouldBe("plain stderr");
    }
}

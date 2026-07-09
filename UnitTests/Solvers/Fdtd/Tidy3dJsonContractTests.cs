using System.Text.Json;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

/// <summary>
/// Verifies the Tidy3D JSON bridge contract: mode injection into the shared
/// request JSON and parsing of check/estimate results.
/// </summary>
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
}

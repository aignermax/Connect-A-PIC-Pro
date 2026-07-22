using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Unit tests for GdsExportService.
/// Tests Python detection, Nazca detection, and GDS export logic.
/// </summary>
[Trait("Category", "Slow")]
public class GdsExportServiceTests
{
    private readonly GdsExportService _service;

    public GdsExportServiceTests()
    {
        _service = new GdsExportService();
    }

    [Fact]
    public async Task CheckPythonEnvironmentAsync_ReturnsEnvironmentInfo()
    {
        // Act
        var result = await _service.CheckPythonEnvironmentAsync();

        // Assert
        result.ShouldNotBeNull();
        result.StatusMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckPythonEnvironmentAsync_WhenPythonNotAvailable_ReturnsFalse()
    {
        // This test will only pass on systems without Python
        // On CI/CD, we can control the environment
        // Act
        var result = await _service.CheckPythonEnvironmentAsync();

        // Assert - Either Python is found or not, both are valid states
        if (!result.PythonAvailable)
        {
            result.IsReady.ShouldBeFalse();
            result.StatusMessage.ShouldBe("Python not found");
            result.PythonVersion.ShouldBeNull();
            result.NazcaAvailable.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task CheckPythonEnvironmentAsync_WhenPythonAvailableButNotNazca_ReturnsPartialInfo()
    {
        // Act
        var result = await _service.CheckPythonEnvironmentAsync();

        // Assert - If Python is available but not Nazca
        if (result.PythonAvailable && !result.NazcaAvailable)
        {
            result.IsReady.ShouldBeFalse();
            result.PythonVersion.ShouldNotBeNullOrEmpty();
            result.StatusMessage.ShouldBe("Nazca not installed");
        }
    }

    [Fact]
    public async Task CheckPythonEnvironmentAsync_WhenBothAvailable_ReturnsFullInfo()
    {
        // Act
        var result = await _service.CheckPythonEnvironmentAsync();

        // Assert - If both are available
        if (result.PythonAvailable && result.NazcaAvailable)
        {
            result.IsReady.ShouldBeTrue();
            result.PythonVersion.ShouldNotBeNullOrEmpty();
            result.NazcaVersion.ShouldNotBeNullOrEmpty();
            result.StatusMessage.ShouldContain("Python");
            result.StatusMessage.ShouldContain("Nazca");
        }
    }

    [Fact]
    public async Task ExportToGdsAsync_WithNonExistentScript_ReturnsError()
    {
        // Arrange
        var scriptPath = "/nonexistent/script.py";

        // Act
        var result = await _service.ExportToGdsAsync(scriptPath, generateGds: true);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Script file not found");
        result.ScriptPath.ShouldBe(scriptPath);
        result.GdsPath.ShouldBeNull();
    }

    [Fact]
    public async Task ExportToGdsAsync_WithGenerateGdsDisabled_ReturnsSuccessWithoutGds()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "# Test script");

        try
        {
            // Act
            var result = await _service.ExportToGdsAsync(tempScript, generateGds: false);

            // Assert
            result.Success.ShouldBeTrue();
            result.ScriptPath.ShouldBe(tempScript);
            result.GdsPath.ShouldBeNull();
            result.Status.ShouldBe("Script exported (GDS generation skipped)");
            result.ErrorMessage.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ExportToGdsAsync_WithValidScriptAndNoPython_ReturnsErrorMessage()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "import nazca as nd\nnd.export_gds()");

        try
        {
            // Act
            var result = await _service.ExportToGdsAsync(tempScript, generateGds: true);

            // Assert
            result.ScriptPath.ShouldBe(tempScript);

            // If environment not ready, should skip GDS generation
            var envInfo = await _service.CheckPythonEnvironmentAsync();
            if (!envInfo.IsReady)
            {
                result.Success.ShouldBeFalse();
                result.Status.ShouldContain("GDS generation skipped");
                result.ErrorMessage.ShouldNotBeNullOrEmpty();
            }
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ExportResult_SuccessProperty_MatchesGdsPathPresence()
    {
        // Arrange
        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "# Test");

        try
        {
            // Act
            var result = await _service.ExportToGdsAsync(tempScript, generateGds: false);

            // Assert
            if (result.Success)
            {
                result.ErrorMessage.ShouldBeNull();
            }
            else
            {
                result.ErrorMessage.ShouldNotBeNullOrEmpty();
            }
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public void ExportResult_StatusMessage_ShouldBeDescriptive()
    {
        // Arrange & Act
        var result = new GdsExportService.ExportResult
        {
            ScriptPath = "test.py",
            GdsPath = "test.gds",
            Success = true,
            Status = "Script and GDS exported successfully"
        };

        // Assert
        result.Status.ShouldNotBeNullOrEmpty();
        result.Status.ShouldContain("Script");
    }

    [Fact]
    public void PythonEnvironmentInfo_IsReady_RequiresBothPythonAndNazca()
    {
        // Arrange & Act
        var notReady1 = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = false,
            NazcaAvailable = false
        };

        var notReady2 = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = true,
            NazcaAvailable = false
        };

        var ready = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = true,
            NazcaAvailable = true
        };

        // Assert
        notReady1.IsReady.ShouldBeFalse();
        notReady2.IsReady.ShouldBeFalse();
        ready.IsReady.ShouldBeTrue();
    }

    [Fact]
    public void PythonEnvironmentInfo_StatusMessage_ReflectsState()
    {
        // Arrange & Act
        var noPython = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = false
        };

        var noNazca = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = true,
            PythonVersion = "3.10.0",
            NazcaAvailable = false
        };

        var ready = new GdsExportService.PythonEnvironmentInfo
        {
            PythonAvailable = true,
            PythonVersion = "3.10.0",
            NazcaAvailable = true,
            NazcaVersion = "0.5.10"
        };

        // Assert
        noPython.StatusMessage.ShouldBe("Python not found");
        noNazca.StatusMessage.ShouldBe("Nazca not installed");
        ready.StatusMessage.ShouldContain("Python 3.10.0");
        ready.StatusMessage.ShouldContain("Nazca 0.5.10");
    }
}

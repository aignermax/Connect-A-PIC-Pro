using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Unit tests for PythonDiscoveryService.
/// Tests Python discovery in venv and system locations.
/// </summary>
[Trait("Category", "Slow")]
public class PythonDiscoveryServiceTests
{
    private readonly PythonDiscoveryService _service;

    public PythonDiscoveryServiceTests()
    {
        _service = new PythonDiscoveryService();
    }

    [Fact]
    public async Task DiscoverPythonWithNazcaAsync_ReturnsListOfInstallations()
    {
        // Act
        var result = await _service.DiscoverPythonWithNazcaAsync();

        // Assert
        result.ShouldNotBeNull();
        // May return empty list if no Python/Nazca installed
        result.ShouldBeOfType<List<PythonDiscoveryService.PythonInstallation>>();
    }

    [Fact]
    public async Task DiscoverPythonWithNazcaAsync_OnlyReturnsInstallationsWithNazca()
    {
        // Act
        var result = await _service.DiscoverPythonWithNazcaAsync();

        // Assert
        // All returned installations should have Nazca
        result.ShouldAllBe(install => install.HasNazca);
    }

    [Fact]
    public async Task CheckPythonInstallation_WithValidPath_ReturnsInstallationInfo()
    {
        // Arrange - Use system Python if available
        var pythonCmd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "python" : "python3";

        // Act
        var result = await _service.CheckPythonInstallation(pythonCmd, "Test");

        // Assert - Either Python is found or not, both are valid
        if (result != null)
        {
            result.Path.ShouldBe(pythonCmd);
            result.Source.ShouldBe("Test");
            result.PythonVersion.ShouldNotBeNullOrEmpty();
            // Nazca may or may not be installed
        }
    }

    [Fact]
    public async Task CheckPythonInstallation_WithInvalidPath_ReturnsNull()
    {
        // Arrange
        var invalidPath = "/nonexistent/python";

        // Act
        var result = await _service.CheckPythonInstallation(invalidPath, "Test");

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void PythonInstallation_HasNazca_ReturnsTrueWhenNazcaVersionSet()
    {
        // Arrange
        var withNazca = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.10.0",
            NazcaVersion = "0.6.1"
        };

        var withoutNazca = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.10.0",
            NazcaVersion = null
        };

        // Assert
        withNazca.HasNazca.ShouldBeTrue();
        withoutNazca.HasNazca.ShouldBeFalse();
    }

    [Fact]
    public void PythonInstallation_DisplayText_IncludesSourceAndVersions()
    {
        // Arrange
        var installation = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/home/user/.venvs/nazca/bin/python",
            Source = "venv: nazca",
            PythonVersion = "3.12.3",
            NazcaVersion = "0.6.1",
            GdsFactoryVersion = "9.5.3"
        };

        // Act
        var displayText = installation.DisplayText;

        // Assert
        displayText.ShouldContain("venv: nazca");
        displayText.ShouldContain("Python 3.12.3");
        displayText.ShouldContain("Nazca 0.6.1");
        displayText.ShouldContain("gdsfactory 9.5.3");
    }

    [Fact]
    public void PythonInstallation_DisplayText_ShowsNotInstalledForAbsentPackages()
    {
        // Both package slots are always shown so a Nazca-only interpreter is
        // distinguishable from a gdsfactory-capable one at a glance (issue #645).
        var installation = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.12.3",
            NazcaVersion = "0.6.1",
            GdsFactoryVersion = null
        };

        // Act
        var displayText = installation.DisplayText;

        // Assert
        displayText.ShouldContain("Nazca 0.6.1");
        displayText.ShouldContain("gdsfactory not installed");
    }

    [Fact]
    public async Task DiscoverPythonWithNazcaAsync_DoesNotReturnDuplicates()
    {
        // Act
        var result = await _service.DiscoverPythonWithNazcaAsync();

        // Assert
        var paths = result.Select(r => r.Path).ToList();
        paths.Count.ShouldBe(paths.Distinct().Count(), "Should not return duplicate Python paths");
    }

    [Fact]
    public async Task CheckPythonInstallation_WithSystemCommand_ReturnsCorrectSource()
    {
        // Arrange
        var pythonCmd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "python" : "python3";
        var expectedSource = "System";

        // Act
        var result = await _service.CheckPythonInstallation(pythonCmd, expectedSource);

        // Assert - If Python is found
        if (result != null)
        {
            result.Source.ShouldBe(expectedSource);
        }
    }

    [Fact]
    public async Task DiscoverPythonWithNazcaAsync_InstalledEntries_PointToRealExecutables()
    {
        // Act
        var result = await _service.DiscoverPythonWithNazcaAsync();

        // Assert - "Installed" entries come from scanning real install dirs, so their
        // Path must be an existing python.exe (never a bare PATH alias). Passes
        // vacuously on machines without such an install.
        foreach (var install in result.Where(r => r.Source == "Installed"))
        {
            File.Exists(install.Path).ShouldBeTrue($"Installed Python should be a real file: {install.Path}");
            install.HasNazca.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task DiscoverPythonWithNazcaAsync_IncludesActiveVenvIfPresent()
    {
        // Arrange - Check if VIRTUAL_ENV is set
        var venvPath = Environment.GetEnvironmentVariable("VIRTUAL_ENV");

        // Act
        var result = await _service.DiscoverPythonWithNazcaAsync();

        // Assert - If running in a venv with Nazca, it should be in the list
        if (!string.IsNullOrEmpty(venvPath))
        {
            var activeVenv = result.FirstOrDefault(r => r.Source == "Active venv");
            // May or may not have Nazca installed in current venv
            if (activeVenv != null)
            {
                activeVenv.HasNazca.ShouldBeTrue();
            }
        }
    }
}

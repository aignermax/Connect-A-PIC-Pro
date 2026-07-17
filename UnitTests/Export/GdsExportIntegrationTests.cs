using CAP_Core.Export;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Integration tests for GdsExportViewModel and GdsExportService.
/// Tests the complete flow from ViewModel to core service.
/// </summary>
public class GdsExportIntegrationTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals
    /// regardless of the runner's locale (LocalizationService.Instance is process-wide).</summary>
    public GdsExportIntegrationTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public async Task ViewModel_CheckEnvironment_UpdatesStatusProperties()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act
        await viewModel.CheckEnvironmentAsync();

        // Assert
        viewModel.PythonStatus.ShouldNotBeNullOrEmpty();
        viewModel.NazcaStatus.ShouldNotBeNullOrEmpty();
        viewModel.PythonStatus.ShouldNotBe("Checking...");
        viewModel.NazcaStatus.ShouldNotBe("Checking...");
        viewModel.IsChecking.ShouldBeFalse();
    }

    [Fact]
    public async Task ViewModel_CheckEnvironment_SetsIsEnvironmentReady()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act
        await viewModel.CheckEnvironmentAsync();

        // Assert
        var envReady = viewModel.IsEnvironmentReady;
        if (envReady)
        {
            viewModel.PythonAvailable.ShouldBeTrue();
            viewModel.NazcaAvailable.ShouldBeTrue();
            viewModel.PythonStatus.ShouldContain("✓");
            viewModel.NazcaStatus.ShouldContain("✓");
        }
        else
        {
            // Either Python or Nazca (or both) not available
            (viewModel.PythonAvailable && viewModel.NazcaAvailable).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ViewModel_ExportScriptToGds_WithGenerateDisabled_SkipsGdsGeneration()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        viewModel.GenerateGdsEnabled = false;

        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "# Test script");

        try
        {
            // Act
            var result = await viewModel.ExportScriptToGdsAsync(tempScript);

            // Assert
            result.Success.ShouldBeTrue();
            result.GdsPath.ShouldBeNull();
            result.Status.ShouldContain("skipped");
            viewModel.LastExportStatus.ShouldBe(result.Status);
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task ViewModel_ExportScriptToGds_WithGenerateEnabled_AttemptsGdsGeneration()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        viewModel.GenerateGdsEnabled = true;

        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "import nazca as nd\nnd.export_gds()");

        try
        {
            // Act
            var result = await viewModel.ExportScriptToGdsAsync(tempScript);

            // Assert
            result.ShouldNotBeNull();
            result.ScriptPath.ShouldBe(tempScript);
            viewModel.LastExportStatus.ShouldNotBeNullOrEmpty();

            // If environment is ready, GDS should be generated
            // If not ready, should get error message
            var envInfo = await service.CheckPythonEnvironmentAsync();
            if (!envInfo.IsReady)
            {
                result.Success.ShouldBeFalse();
                result.ErrorMessage.ShouldNotBeNullOrEmpty();
            }
        }
        finally
        {
            File.Delete(tempScript);
            var gdsPath = Path.ChangeExtension(tempScript, ".gds");
            if (File.Exists(gdsPath))
            {
                File.Delete(gdsPath);
            }
        }
    }

    [Fact]
    public async Task ViewModel_ExportScriptToGds_UpdatesLastExportStatus()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "# Test");

        try
        {
            // Act
            var initialStatus = viewModel.LastExportStatus;
            await viewModel.ExportScriptToGdsAsync(tempScript);
            var updatedStatus = viewModel.LastExportStatus;

            // Assert
            updatedStatus.ShouldNotBe(initialStatus);
            updatedStatus.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task Integration_CheckEnvironmentThenExport_WorksCorrectly()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        var tempScript = Path.GetTempFileName();
        File.WriteAllText(tempScript, "# Test script\nimport nazca as nd\nnd.export_gds()");

        try
        {
            // Act - Check environment first
            await viewModel.CheckEnvironmentAsync();

            // Then attempt export
            viewModel.GenerateGdsEnabled = viewModel.IsEnvironmentReady;
            var result = await viewModel.ExportScriptToGdsAsync(tempScript);

            // Assert
            result.ScriptPath.ShouldBe(tempScript);
            viewModel.LastExportStatus.ShouldNotBeNullOrEmpty();

            // The test adapts to the actual environment state
            // If Python+Nazca are available, GDS generation will be enabled and attempted
            // If not available, GDS generation will be disabled
            if (viewModel.IsEnvironmentReady && viewModel.GenerateGdsEnabled)
            {
                // Environment is ready and GDS generation was attempted
                // Success depends on whether Nazca script executed properly
                result.ShouldNotBeNull();
            }
            else
            {
                // GDS generation was disabled - script-only export should succeed
                result.Success.ShouldBeTrue();
                result.GdsPath.ShouldBeNull();
            }
        }
        finally
        {
            File.Delete(tempScript);
            var gdsPath = Path.ChangeExtension(tempScript, ".gds");
            if (File.Exists(gdsPath))
            {
                File.Delete(gdsPath);
            }
        }
    }

    [Fact]
    public void ViewModel_DefaultState_HasCorrectInitialValues()
    {
        // Arrange & Act
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Assert
        viewModel.PythonAvailable.ShouldBeFalse();
        viewModel.NazcaAvailable.ShouldBeFalse();
        viewModel.GenerateGdsEnabled.ShouldBeTrue();
        viewModel.IsChecking.ShouldBeFalse();
        viewModel.LastExportStatus.ShouldBeEmpty();
        viewModel.PythonStatus.ShouldBe("Checking...");
        viewModel.NazcaStatus.ShouldBe("Checking...");
    }
}

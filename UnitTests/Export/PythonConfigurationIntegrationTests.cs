using CAP_Core.Export;
using CAP.Avalonia.ViewModels.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Integration tests for Python configuration.
/// Tests Core (GdsExportService, PythonDiscoveryService) + ViewModel (GdsExportViewModel) integration.
/// </summary>
public class PythonConfigurationIntegrationTests
{
    [Fact]
    public async Task GdsExportViewModel_SearchForPython_PopulatesAvailablePythons()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act
        await viewModel.SearchForPythonAsync();

        // Assert
        viewModel.AvailablePythons.ShouldNotBeNull();
        // May be empty if no Python with Nazca is installed
        if (viewModel.AvailablePythons.Count > 0)
        {
            viewModel.AvailablePythons.ShouldAllBe(p => !string.IsNullOrEmpty(p.Path));
            viewModel.AvailablePythons.ShouldAllBe(p => p.HasNazca);
        }
    }

    [Fact]
    public async Task GdsExportViewModel_SetPythonPath_UpdatesService()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        var customPath = "/custom/python";

        // Act
        await viewModel.SetPythonPathAsync(customPath);

        // Assert
        viewModel.CustomPythonPath.ShouldBe(customPath);
        service.GetCurrentPythonPath().ShouldBe(customPath);
        viewModel.PythonPathSource.ShouldBe("Custom");
    }

    [Fact]
    public async Task GdsExportViewModel_SelectPython_UpdatesPathAndChecksEnvironment()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        var installation = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.10.0",
            NazcaVersion = "0.6.1"
        };

        // Act
        await viewModel.SelectPython(installation);

        // Assert
        viewModel.CustomPythonPath.ShouldBe(installation.Path);
        viewModel.PythonPathSource.ShouldBe(installation.Source);
        service.GetCurrentPythonPath().ShouldBe(installation.Path);
    }

    [Fact]
    public void GdsExportViewModel_Initialize_LoadsSavedPath()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        var savedPath = "/saved/python";

        // Act
        viewModel.Initialize(savedPath);

        // Assert
        viewModel.CustomPythonPath.ShouldBe(savedPath);
        service.GetCurrentPythonPath().ShouldBe(savedPath);
        viewModel.PythonPathSource.ShouldBe("Custom");
    }

    [Fact]
    public void GdsExportViewModel_Initialize_WithNullPath_UsesDefault()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act
        viewModel.Initialize(null);

        // Assert
        viewModel.CustomPythonPath.ShouldBeEmpty();
        // Service should use default system Python
        var currentPath = service.GetCurrentPythonPath();
        // The default now resolves to an absolute interpreter path when one exists on a
        // well-known location (the macOS Finder-PATH fix); accept either the bare fallback
        // name or a resolved python/python3 path.
        System.IO.Path.GetFileNameWithoutExtension(currentPath).ShouldBeOneOf("python", "python3");
    }

    [Fact]
    public async Task GdsExportViewModel_SearchForPython_AutoSelectsFirst()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act
        await viewModel.SearchForPythonAsync();

        // Assert - If any Python found, first should be auto-selected
        if (viewModel.AvailablePythons.Count > 0)
        {
            var first = viewModel.AvailablePythons[0];
            viewModel.CustomPythonPath.ShouldBe(first.Path);
            viewModel.PythonPathSource.ShouldBe(first.Source);
        }
    }

    [Fact]
    public void GdsExportViewModel_OnPythonPathChanged_InvokesCallback()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);
        string? capturedPath = null;
        viewModel.OnPythonPathChanged = path => capturedPath = path;
        var installation = new PythonDiscoveryService.PythonInstallation
        {
            Path = "/test/python",
            Source = "Test"
        };

        // Act
        viewModel.SelectPython(installation).Wait();

        // Assert
        capturedPath.ShouldBe(installation.Path);
    }

    [Fact]
    public async Task EndToEnd_DiscoverSelectAndCheck_WorksTogether()
    {
        // Arrange
        var service = new GdsExportService();
        var viewModel = new GdsExportViewModel(service);

        // Act - Full workflow
        await viewModel.SearchForPythonAsync();

        // If Python with Nazca was found
        if (viewModel.AvailablePythons.Count > 0)
        {
            var selected = viewModel.AvailablePythons[0];
            await viewModel.SelectPython(selected);
            await viewModel.CheckEnvironmentAsync();

            // Assert
            viewModel.CustomPythonPath.ShouldBe(selected.Path);
            viewModel.PythonAvailable.ShouldBeTrue();
            viewModel.NazcaAvailable.ShouldBeTrue();
            viewModel.IsEnvironmentReady.ShouldBeTrue();
            viewModel.PythonStatus.ShouldContain("✓");
            viewModel.NazcaStatus.ShouldContain("✓");
        }
    }

    [Fact]
    public async Task GdsExportService_CustomPath_UsedInEnvironmentCheck()
    {
        // Arrange
        var service = new GdsExportService();
        var systemPython = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "python" : "python3";

        // Act - Set custom path and check
        service.SetCustomPythonPath(systemPython);
        var result = await service.CheckPythonEnvironmentAsync();

        // Assert - Should use custom path for check
        service.GetCurrentPythonPath().ShouldBe(systemPython);
        // Result depends on whether Python is installed
        if (result.PythonAvailable)
        {
            result.PythonVersion.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public void GdsExportService_DefaultPath_ChangesWithCustomPath()
    {
        // Arrange
        var service = new GdsExportService();
        var originalDefault = service.GetCurrentPythonPath();

        // Act
        var customPath = "/custom/python";
        service.SetCustomPythonPath(customPath);
        var afterCustom = service.GetCurrentPythonPath();

        service.SetCustomPythonPath(null);
        var afterReset = service.GetCurrentPythonPath();

        // Assert
        afterCustom.ShouldBe(customPath);
        afterReset.ShouldBe(originalDefault);
    }
}

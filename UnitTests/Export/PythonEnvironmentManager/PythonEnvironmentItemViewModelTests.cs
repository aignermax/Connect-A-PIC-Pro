using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="PythonEnvironmentItemViewModel"/> — the bindable
/// wrapper that renders one environment row in the Python Environments
/// settings page (status badge + details line).
/// </summary>
public class PythonEnvironmentItemViewModelTests
{
    private static PythonEnvironment HealthyEnv() => new()
    {
        Name = "nazca",
        VenvPath = "/tmp/lunima-test-env",
        Status = PythonEnvironmentStatus.Healthy,
        PythonVersion = "3.11.15",
        NazcaVersion = "0.6.1",
        HasPyclipper = true,
    };

    [Fact]
    public void Details_WithGdsFactoryVersion_ShowsGdsFactory()
    {
        var env = HealthyEnv();
        env.GdsFactoryVersion = "9.5.7";

        var vm = new PythonEnvironmentItemViewModel(env);

        vm.Details.ShouldContain("gdsfactory 9.5.7");
    }

    [Fact]
    public void Details_WithoutGdsFactory_OmitsGdsFactory()
    {
        var vm = new PythonEnvironmentItemViewModel(HealthyEnv());

        vm.Details.ShouldNotContain("gdsfactory");
    }

    [Fact]
    public void Details_ListsPythonNazcaAndPyclipper()
    {
        var vm = new PythonEnvironmentItemViewModel(HealthyEnv());

        vm.Details.ShouldContain("Python 3.11.15");
        vm.Details.ShouldContain("Nazca 0.6.1");
        vm.Details.ShouldContain("pyclipper ✓");
    }
}

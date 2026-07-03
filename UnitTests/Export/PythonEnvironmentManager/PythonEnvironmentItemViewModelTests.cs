using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="PythonEnvironmentItemViewModel"/>'s detail line. gdsfactory is
/// always surfaced — including "not installed" — so the user can distinguish a Nazca-only
/// environment from a gdsfactory-capable one at a glance (issue #645).
/// </summary>
public class PythonEnvironmentItemViewModelTests
{
    [Fact]
    public void Details_WhenGdsFactoryPresent_ShowsItsVersion()
    {
        var env = new PythonEnvironment
        {
            Name = "gf", VenvPath = "x",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1", GdsFactoryVersion = "9.5.3",
        };

        new PythonEnvironmentItemViewModel(env).Details.ShouldContain("gdsfactory 9.5.3");
    }

    [Fact]
    public void Details_WhenGdsFactoryAbsent_ShowsNotInstalled()
    {
        var env = new PythonEnvironment
        {
            Name = "nazca-only", VenvPath = "x",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1", GdsFactoryVersion = null,
        };

        new PythonEnvironmentItemViewModel(env).Details.ShouldContain("gdsfactory not installed");
    }
}

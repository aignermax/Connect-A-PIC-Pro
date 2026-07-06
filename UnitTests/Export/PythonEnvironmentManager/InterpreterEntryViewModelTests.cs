using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="InterpreterEntryViewModel"/>, the unified list row (issue #645).
/// A managed row always spells out Nazca and gdsfactory — including "not installed" — so it
/// is distinguishable from a gdsfactory-capable interpreter at a glance; a system row reuses
/// the discovery display text and exposes no managed actions.
/// </summary>
public class InterpreterEntryViewModelTests
{
    [Fact]
    public void ManagedRow_WithGdsFactory_ShowsBothVersions_AndIsManaged()
    {
        var env = new PythonEnvironment
        {
            Name = "nazca", VenvPath = "x",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1", GdsFactoryVersion = "9.5.3",
        };

        var row = new InterpreterEntryViewModel(env, isActive: true);

        row.IsManaged.ShouldBeTrue();
        row.ManagedName.ShouldBe("nazca");
        row.DisplayText.ShouldContain("Nazca 0.6.1");
        row.DisplayText.ShouldContain("gdsfactory 9.5.3");
        row.HasStatusBadge.ShouldBeTrue();
    }

    [Fact]
    public void ManagedRow_WithoutGdsFactory_ShowsNotInstalled()
    {
        var env = new PythonEnvironment
        {
            Name = "nazca-only", VenvPath = "x",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1", GdsFactoryVersion = null,
        };

        new InterpreterEntryViewModel(env, isActive: false)
            .DisplayText.ShouldContain("gdsfactory not installed");
    }

    [Fact]
    public void SystemRow_IsNotManaged_AndHasNoBadge()
    {
        var install = new CAP_Core.Export.PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3.12", Source = "System",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1",
        };

        var row = new InterpreterEntryViewModel(install, isActive: false);

        row.IsManaged.ShouldBeFalse();
        row.ManagedName.ShouldBeNull();
        row.HasStatusBadge.ShouldBeFalse();
        row.Path.ShouldBe("/usr/bin/python3.12");
    }
}

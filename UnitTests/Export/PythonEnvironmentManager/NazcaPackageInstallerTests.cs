using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Pins the package set installed into managed environments. The SiEPIC
/// fixed-cell render path of the PDK preview imports <c>klayout</c> and
/// <c>siepic_ebeam_pdk</c>; a managed env without them fails every SiEPIC
/// component with "not installed in this Python environment".
/// </summary>
public class NazcaPackageInstallerTests
{
    [Theory]
    [InlineData("pyclipper")]
    [InlineData("klayout")]
    [InlineData("siepic_ebeam_pdk")]
    public void AdditionalPackages_ContainRequiredRenderDependencies(string package)
    {
        NazcaPackageInstaller.AdditionalPackages.ShouldContain(package);
    }

    [Theory]
    [InlineData("gdsfactory")]
    [InlineData("ubcpdk")]
    [InlineData("cspdk")]   // CornerStone SiN — components import cspdk.sin300 (#570, field bug)
    public void GdsFactoryPackages_ContainTheFoundryPdksTheExportAndPreviewImport(string package)
    {
        NazcaPackageInstaller.GdsFactoryPackages.ShouldContain(package);
    }
}

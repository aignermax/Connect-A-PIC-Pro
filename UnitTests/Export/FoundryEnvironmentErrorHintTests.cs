using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Verifies the classification of raw Python render errors into actionable
/// foundry-package hints (field bug: a raw "module 'cspdk' has no attribute
/// 'sin300'" told the user nothing about how to fix their environment/code).
/// </summary>
public class FoundryEnvironmentErrorHintTests
{
    [Theory]
    [InlineData("No module named 'cspdk'", "cspdk")]
    [InlineData("ModuleNotFoundError: No module named 'cspdk.sin300'", "cspdk")]
    [InlineData("No module named 'ubcpdk'", "ubcpdk")]
    [InlineData("No module named 'siepic_ebeam_pdk'", "siepic_ebeam_pdk")]
    [InlineData("No module named 'gdsfactory'", "gdsfactory")]
    [InlineData("No module named 'nazca'", "nazca")]
    public void Describe_missingFoundryModule_pointsAtPythonEnvironmentsSettings(string raw, string package)
    {
        var hint = FoundryEnvironmentErrorHint.Describe(raw);

        hint.ShouldNotBeNull();
        hint!.ShouldContain($"'{package}'");
        hint.ShouldContain("Settings → Python Environments");
        hint.ShouldContain("Error Console");
    }

    [Fact]
    public void Describe_missingAttributeOnFoundryModule_suggestsUpdateOrSubmoduleImport()
    {
        // The exact error of the CornerStone field report.
        var hint = FoundryEnvironmentErrorHint.Describe("module 'cspdk' has no attribute 'sin300'");

        hint.ShouldNotBeNull();
        hint!.ShouldContain("'cspdk'");
        hint.ShouldContain("cspdk.sin300");
        hint.ShouldContain("Settings → Python Environments");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SyntaxError: invalid syntax")]
    [InlineData("No module named 'mymodule'")]                       // user's own import
    [InlineData("module 'mymodule' has no attribute 'whatever'")]    // user's own module
    [InlineData("Preview script timed out after 90s.")]
    public void Describe_unrelatedErrors_returnNull_soTheRawErrorStaysVisible(string? raw)
    {
        FoundryEnvironmentErrorHint.Describe(raw).ShouldBeNull();
    }
}

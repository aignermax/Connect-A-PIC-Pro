using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="EnvironmentNaming"/> — the validation gate that keeps
/// user-supplied environment names from escaping the managed envs directory
/// (they flow into <c>Path.Combine</c> and a recursive <c>Directory.Delete</c>).
/// </summary>
public class EnvironmentNamingTests
{
    [Theory]
    [InlineData("nazca")]
    [InlineData("my-env_2")]
    [InlineData("py3.11")]
    [InlineData("A")]
    public void IsValidName_PlainNames_AreAccepted(string name)
    {
        EnvironmentNaming.IsValidName(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(".hidden")]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData(@"C:\Windows")]
    [InlineData("/etc")]
    [InlineData("a b")]
    [InlineData("a<b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidName_PathLikeOrInvalidNames_AreRejected(string? name)
    {
        EnvironmentNaming.IsValidName(name).ShouldBeFalse();
    }

    [Fact]
    public void IsValidName_OverlongName_IsRejected()
    {
        EnvironmentNaming.IsValidName(new string('a', 65)).ShouldBeFalse();
        EnvironmentNaming.IsValidName(new string('a', 64)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("3")]
    [InlineData("3.11")]
    [InlineData("3.11.4")]
    public void IsValidPythonVersion_PlainVersions_AreAccepted(string version)
    {
        EnvironmentNaming.IsValidPythonVersion(version).ShouldBeTrue();
    }

    [Theory]
    [InlineData("3.11 --seed")]   // argument injection via the version field
    [InlineData("latest")]
    [InlineData("3.11;rm -rf ~")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidPythonVersion_NonVersionInput_IsRejected(string? version)
    {
        EnvironmentNaming.IsValidPythonVersion(version).ShouldBeFalse();
    }

    [Theory]
    [InlineData("3.11", "py3.11")]
    [InlineData("3", "py3")]
    [InlineData("3.11.4", "py3.11.4")]
    [InlineData(" 3.12 ", "py3.12")]   // surrounding whitespace is trimmed
    public void GenerateName_NoCollision_UsesVersionBasedName(string version, string expected)
    {
        EnvironmentNaming.GenerateName(version, _ => false).ShouldBe(expected);
    }

    [Fact]
    public void GenerateName_Collision_AppendsNumericSuffix()
    {
        var taken = new HashSet<string> { "py3.11", "py3.11-2" };

        EnvironmentNaming.GenerateName("3.11", taken.Contains).ShouldBe("py3.11-3");
    }

    [Theory]
    [InlineData("3.11")]
    [InlineData("3.11.4")]
    [InlineData("3")]
    public void GenerateName_Result_IsValidByConstruction(string version)
    {
        // Even the suffixed collision names must pass the security validation gate.
        var first = EnvironmentNaming.GenerateName(version, _ => false);
        var suffixed = EnvironmentNaming.GenerateName(version, n => n == first);

        EnvironmentNaming.IsValidName(first).ShouldBeTrue();
        EnvironmentNaming.IsValidName(suffixed).ShouldBeTrue();
        suffixed.ShouldNotBe(first);
    }

    [Theory]
    [InlineData("3.11 --seed")]
    [InlineData("latest")]
    [InlineData("")]
    public void GenerateName_InvalidVersion_Throws(string version)
    {
        Should.Throw<ArgumentException>(() =>
            EnvironmentNaming.GenerateName(version, _ => false));
    }

    [Fact]
    public void IsInsideDirectory_ChildPath_ReturnsTrue()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "lunima-envs");

        EnvironmentNaming.IsInsideDirectory(baseDir, Path.Combine(baseDir, "my-env"))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("..")]           // resolves to the parent of the base dir
    [InlineData("../sibling")]   // escapes sideways
    public void IsInsideDirectory_TraversalPath_ReturnsFalse(string relative)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "lunima-envs");
        var escaped = Path.Combine(baseDir, relative);

        EnvironmentNaming.IsInsideDirectory(baseDir, escaped).ShouldBeFalse();
    }

    [Fact]
    public void IsInsideDirectory_TheBaseDirItself_ReturnsFalse()
    {
        // Deleting the base dir itself would take every other environment with it.
        var baseDir = Path.Combine(Path.GetTempPath(), "lunima-envs");

        EnvironmentNaming.IsInsideDirectory(baseDir, baseDir).ShouldBeFalse();
    }

    [Fact]
    public void IsInsideDirectory_SiblingWithCommonPrefix_ReturnsFalse()
    {
        // "lunima-envs-evil" starts with "lunima-envs" as a raw string but is a sibling.
        var baseDir = Path.Combine(Path.GetTempPath(), "lunima-envs");
        var sibling = Path.Combine(Path.GetTempPath(), "lunima-envs-evil", "x");

        EnvironmentNaming.IsInsideDirectory(baseDir, sibling).ShouldBeFalse();
    }
}

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

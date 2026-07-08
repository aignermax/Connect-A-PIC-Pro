using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Export;

/// <summary>
/// Tests for the export-filename guard: a Nazca script saved as e.g. <c>re.py</c>
/// shadows Python's stdlib <c>re</c> module (the script directory is first on
/// <c>sys.path</c>), which breaks numpy/nazca imports with a cryptic circular-import
/// error no matter which interpreter is selected.
/// </summary>
public class PythonModuleShadowingTests
{
    [Theory]
    [InlineData("re")]
    [InlineData("RE")]          // Windows file systems are case-insensitive
    [InlineData("os")]
    [InlineData("json")]
    [InlineData("inspect")]
    [InlineData("numpy")]       // Nazca dependency — shadowing it breaks the import chain too
    [InlineData("pandas")]
    [InlineData("nazca")]
    public void ShadowsPythonModule_KnownModuleNames_ReturnsTrue(string stem)
    {
        PythonModuleShadowing.ShadowsPythonModule(stem).ShouldBeTrue();
    }

    [Theory]
    [InlineData("chip1")]
    [InlineData("my_design")]
    [InlineData("mzi-export")]
    [InlineData("")]
    public void ShadowsPythonModule_HarmlessNames_ReturnsFalse(string stem)
    {
        PythonModuleShadowing.ShadowsPythonModule(stem).ShouldBeFalse();
    }
}

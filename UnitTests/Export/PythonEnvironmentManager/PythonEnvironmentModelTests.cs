using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Unit tests for the <see cref="PythonEnvironment"/> model and related enums.
/// </summary>
public class PythonEnvironmentModelTests
{
    [Fact]
    public void PythonExecutable_Windows_UsesScriptsFolder()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return; // Skip on non-Windows

        var env = new PythonEnvironment { VenvPath = @"C:\Lunima\envs\test" };
        env.PythonExecutable.ShouldBe(@"C:\Lunima\envs\test\Scripts\python.exe");
    }

    [Fact]
    public void PythonExecutable_Unix_UsesBinFolder()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            return; // Skip on Windows

        var env = new PythonEnvironment { VenvPath = "/home/user/.local/share/Lunima/envs/test" };
        env.PythonExecutable.ShouldBe("/home/user/.local/share/Lunima/envs/test/bin/python");
    }

    [Theory]
    [InlineData(PythonEnvironmentStatus.Healthy, true)]
    [InlineData(PythonEnvironmentStatus.Broken, false)]
    [InlineData(PythonEnvironmentStatus.Creating, false)]
    [InlineData(PythonEnvironmentStatus.Installing, false)]
    [InlineData(PythonEnvironmentStatus.Unknown, false)]
    public void IsHealthy_ReflectsStatus(PythonEnvironmentStatus status, bool expected)
    {
        var env = new PythonEnvironment { Status = status };
        env.IsHealthy.ShouldBe(expected);
    }

    [Fact]
    public void DefaultStatus_IsUnknown()
    {
        var env = new PythonEnvironment();
        env.Status.ShouldBe(PythonEnvironmentStatus.Unknown);
    }

    [Fact]
    public void DefaultName_IsEmpty()
    {
        var env = new PythonEnvironment();
        env.Name.ShouldBeEmpty();
    }
}

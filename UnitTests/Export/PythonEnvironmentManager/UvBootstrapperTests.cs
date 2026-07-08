using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Unit tests for <see cref="UvBootstrapper"/> — tests that can run without
/// network access or an actual uv installation (pure logic tests).
/// </summary>
public class UvBootstrapperTests
{
    private static readonly ProcessLaunchFactory Factory = ProcessLaunchFactory.CreateDefault();

    [Fact]
    public void EnvironmentsBaseDir_IsUnderLunimaAppData()
    {
        var dir = UvBootstrapper.EnvironmentsBaseDir;

        dir.ShouldNotBeNullOrWhiteSpace();
        dir.ShouldContain("Lunima");
        dir.ShouldContain("envs");
    }

    [Fact]
    public async Task RunProcessAsync_EchoCommand_ReturnsOutput()
    {
        // Run a simple cross-platform command to verify the process runner works
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo", "hello" })
            : ("echo", new[] { "hello" });

        var (exitCode, output, _) = await UvBootstrapper.RunProcessAsync(
            Factory, fileName, args, CancellationToken.None, timeoutMs: 10_000);

        exitCode.ShouldBe(0);
        output.Trim().ShouldContain("hello");
    }

    [Fact]
    public async Task RunProcessAsync_WithCancelledToken_ThrowsCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel(); // immediately cancelled

        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping", "-n", "5", "127.0.0.1" })
            : ("sleep", new[] { "5" });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            UvBootstrapper.RunProcessAsync(Factory, fileName, args, cts.Token, timeoutMs: 30_000));
    }

    [Fact]
    public async Task RunProcessAsync_TimeoutWithoutUserCancel_ReturnsNonZeroInsteadOfThrowing()
    {
        // A pure timeout (no user cancellation) must be reported as a failed exit code
        // so callers surface "timed out" instead of misreporting a cancellation.
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping", "-n", "10", "127.0.0.1" })
            : ("sleep", new[] { "10" });

        var (exitCode, _, error) = await UvBootstrapper.RunProcessAsync(
            Factory, fileName, args, CancellationToken.None, timeoutMs: 500);

        exitCode.ShouldNotBe(0);
        error.ShouldContain("timed out");
    }

    [Fact]
    public void DefaultPythonVersion_IsValidVersionString()
    {
        var version = UvBootstrapper.DefaultPythonVersion;
        version.ShouldNotBeNullOrWhiteSpace();
        version.ShouldMatch(@"^\d+\.\d+");
    }
}

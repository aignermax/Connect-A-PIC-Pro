using System.Diagnostics;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// Deterministic unit tests for <see cref="ExecutablePathProber"/> and
/// <see cref="ProcessLaunchFactory"/>. No external executables are required;
/// all file-existence assertions use real temp files created and cleaned up
/// within each test.
/// </summary>
public class ProcessLaunchFactoryTests : IDisposable
{
    // ─── State ────────────────────────────────────────────────────────────────

    private readonly ExecutablePathProber _prober = new();
    private readonly ProcessLaunchFactory _factory;
    private readonly List<string> _tempFiles = new();
    private readonly List<string> _tempDirs = new();

    public ProcessLaunchFactoryTests()
    {
        _factory = new ProcessLaunchFactory(_prober);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates a real temp file and registers it for deletion.</summary>
    private string MakeTempFile()
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ExecutablePathProber.FirstExisting
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FirstExisting_ReturnsFirstCandidateThatExistsOnDisk()
    {
        // Arrange – two real temp files; first should win.
        var first  = MakeTempFile();
        var second = MakeTempFile();

        // Act
        var result = _prober.FirstExisting(new[] { first, second });

        // Assert
        result.ShouldBe(first,
            "FirstExisting must return the first path in the sequence for which File.Exists is true");
    }

    [Fact]
    public void FirstExisting_SkipsNonExistentCandidatesAndReturnsFirstExisting()
    {
        // Arrange – a path that definitely does not exist, followed by a real file.
        var bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var real  = MakeTempFile();

        // Act
        var result = _prober.FirstExisting(new[] { bogus, real });

        // Assert
        result.ShouldBe(real,
            "FirstExisting must skip missing paths and return the first that actually exists");
    }

    [Fact]
    public void FirstExisting_ReturnsNullWhenNoCandidateExists()
    {
        // Arrange – paths that are certain never to exist.
        var bogus1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_a");
        var bogus2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_b");

        // Act
        var result = _prober.FirstExisting(new[] { bogus1, bogus2 });

        // Assert
        result.ShouldBeNull("FirstExisting must return null when no candidate file exists");
    }

    [Fact]
    public void FirstExisting_ReturnsNullForEmptySequence()
    {
        var result = _prober.FirstExisting(Array.Empty<string>());

        result.ShouldBeNull("FirstExisting must return null for an empty candidate list");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ExecutablePathProber.AugmentedPath
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AugmentedPath_OnMacOS_PrependsHomebrewAndUsrLocalBin()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var result = _prober.AugmentedPath("/usr/bin");

        // AugmentedPath must prepend /opt/homebrew/bin and /usr/local/bin on macOS.
        result.ShouldContain("/opt/homebrew/bin");
        result.ShouldContain("/usr/local/bin");
    }

    [Fact]
    public void AugmentedPath_OnMacOS_PrependedDirsAppearBeforeExistingDirs()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var result = _prober.AugmentedPath("/usr/bin");
        var parts  = result.Split(Path.PathSeparator);

        var brewIdx  = Array.IndexOf(parts, "/opt/homebrew/bin");
        var localIdx = Array.IndexOf(parts, "/usr/local/bin");
        var usrIdx   = Array.IndexOf(parts, "/usr/bin");

        brewIdx.ShouldBeGreaterThanOrEqualTo(0,
            "/opt/homebrew/bin must be present in the augmented PATH");
        localIdx.ShouldBeGreaterThanOrEqualTo(0,
            "/usr/local/bin must be present in the augmented PATH");
        brewIdx.ShouldBeLessThan(usrIdx,
            "/opt/homebrew/bin must precede the original /usr/bin entry");
    }

    [Fact]
    public void AugmentedPath_OnMacOS_IsIdempotent_DoesNotDuplicateAlreadyPresentDirs()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Pre-seed the PATH with both macOS dirs already present.
        var seed   = "/opt/homebrew/bin" + Path.PathSeparator + "/usr/local/bin" + Path.PathSeparator + "/usr/bin";
        var first  = _prober.AugmentedPath(seed);
        var second = _prober.AugmentedPath(first);

        first.ShouldBe(second,
            "AugmentedPath must be idempotent: calling it twice must not alter the result");

        var parts = first.Split(Path.PathSeparator);
        parts.Count(p => p == "/opt/homebrew/bin").ShouldBe(1,
            "/opt/homebrew/bin must appear exactly once even if already in PATH");
        parts.Count(p => p == "/usr/local/bin").ShouldBe(1,
            "/usr/local/bin must appear exactly once even if already in PATH");
    }

    [Fact]
    public void AugmentedPath_OnMacOS_HandlesNullInput()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var result = _prober.AugmentedPath(null);

        result.ShouldNotBeNull("AugmentedPath must not return null for a null input");
        // AugmentedPath must still inject macOS dirs when input is null.
        result.ShouldContain("/opt/homebrew/bin");
    }

    [Fact]
    public void AugmentedPath_OnMacOS_HandlesEmptyStringInput()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var result = _prober.AugmentedPath(string.Empty);

        result.ShouldNotBeNullOrEmpty("AugmentedPath must produce a non-empty string for empty input on macOS");
        // AugmentedPath must inject macOS dirs even when input is empty.
        result.ShouldContain("/opt/homebrew/bin");
    }

    [Fact]
    public void AugmentedPath_OnNonMacOS_ReturnsInputUnchanged()
    {
        if (OperatingSystem.IsMacOS()) return;

        const string existingPath = "/usr/bin:/usr/local/bin";
        var result = _prober.AugmentedPath(existingPath);

        result.ShouldBe(existingPath,
            "AugmentedPath must return the existing PATH unchanged on non-macOS platforms");
    }

    [Fact]
    public void AugmentedPath_OnNonMacOS_NullInputReturnsEmptyString()
    {
        if (OperatingSystem.IsMacOS()) return;

        var result = _prober.AugmentedPath(null);

        result.ShouldBe(string.Empty,
            "AugmentedPath must return empty string (not null) when input is null on non-macOS platforms");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ProcessLaunchFactory.ResolveExecutable
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveExecutable_RootedPathToExistingFile_PassesThroughUnchanged()
    {
        // Arrange – a rooted temp file that definitely exists on disk.
        var tempFile = MakeTempFile();
        tempFile.ShouldNotBeNullOrEmpty();
        Path.IsPathRooted(tempFile).ShouldBeTrue("Temp file must be an absolute path for this test to be meaningful");

        // Act
        var result = _factory.ResolveExecutable(tempFile);

        // Assert
        result.ShouldBe(tempFile,
            "ResolveExecutable must return an already-rooted, existing path as-is");
    }

    [Fact]
    public void ResolveExecutable_BogusBareName_ReturnsBareNameUnchanged()
    {
        // Arrange – a random name that definitely isn't on disk.
        const string bogus = "totally-nonexistent-tool-xyzzy-12345";

        // Act
        var result = _factory.ResolveExecutable(bogus);

        // Assert
        result.ShouldBe(bogus,
            "ResolveExecutable must return the bare name unchanged so that Process.Start can " +
            "perform its own OS PATH lookup");
    }

    [Fact]
    public void ResolveExecutable_NullOrEmptyCommand_ReturnsCommandUnchanged()
    {
        _factory.ResolveExecutable(string.Empty).ShouldBe(string.Empty,
            "ResolveExecutable must not throw for an empty command");
    }

    [Fact]
    public void ResolveExecutable_BarePython3_OnWindows_FallsBackToPython()
    {
        if (!OperatingSystem.IsWindows()) return;

        // On Windows a bare `python3` is typically the Microsoft Store execution-alias stub
        // (or absent); the conventional command is `python`. When no well-known interpreter is
        // found, ResolveExecutable must return `python`, not the unchanged `python3`. Regression
        // guard for the macOS-port change that made well-known probing macOS-only.
        _factory.ResolveExecutable("python3").ShouldBe("python",
            "On Windows a bare python3 must resolve to the conventional 'python' command");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ProcessLaunchFactory.TryBuild
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryBuild_ValidCommand_ReturnsTrue()
    {
        var ok = _factory.TryBuild(
            "some-tool",
            new[] { "--version" },
            workingDirectory: null,
            extraEnv: null,
            out _,
            out var error);

        ok.ShouldBeTrue("TryBuild must return true for a non-empty command");
        error.ShouldBeNull("error output parameter must be null on success");
    }

    [Fact]
    public void TryBuild_ArgumentListIsPopulatedInOrder_AndRawArgumentsStringIsEmpty()
    {
        var args = new[] { "--input", "foo.gds", "--output", "bar.gds" };

        _factory.TryBuild(
            "gds-tool",
            args,
            workingDirectory: null,
            extraEnv: null,
            out var psi,
            out _);

        // ArgumentList must contain all args in the original order.
        psi.ArgumentList.Count.ShouldBe(args.Length,
            "ArgumentList must contain exactly the supplied arguments");
        for (int i = 0; i < args.Length; i++)
            psi.ArgumentList[i].ShouldBe(args[i],
                $"ArgumentList[{i}] must match the supplied argument at index {i}");

        // The raw .Arguments string must remain empty — we never hand-roll it.
        psi.Arguments.ShouldBe(string.Empty,
            "ProcessStartInfo.Arguments must be empty; all arguments go through ArgumentList");
    }

    [Fact]
    public void TryBuild_UseShellExecuteIsFalse()
    {
        _factory.TryBuild(
            "any-tool",
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: null,
            out var psi,
            out _);

        psi.UseShellExecute.ShouldBeFalse(
            "UseShellExecute must be false so stdout/stderr can be redirected");
    }

    [Fact]
    public void TryBuild_WorkingDirectoryIsSetToSuppliedValue()
    {
        var dir = Path.GetTempPath();

        _factory.TryBuild(
            "any-tool",
            Array.Empty<string>(),
            workingDirectory: dir,
            extraEnv: null,
            out var psi,
            out _);

        psi.WorkingDirectory.ShouldBe(dir,
            "WorkingDirectory must match the explicitly supplied value");
    }

    [Fact]
    public void TryBuild_NullWorkingDirectory_FallsBackToAppContextBaseDirectory()
    {
        _factory.TryBuild(
            "any-tool",
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: null,
            out var psi,
            out _);

        psi.WorkingDirectory.ShouldBe(AppContext.BaseDirectory,
            "WorkingDirectory must fall back to AppContext.BaseDirectory when null is supplied");
    }

    [Fact]
    public void TryBuild_ExtraEnvVariablesAreMergedIntoEnvironmentVariables()
    {
        var extraEnv = new Dictionary<string, string>
        {
            ["PYTHONPATH"] = "/my/site-packages",
            ["MY_CUSTOM_VAR"] = "hello",
        };

        _factory.TryBuild(
            "python3",
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: extraEnv,
            out var psi,
            out _);

        psi.EnvironmentVariables["PYTHONPATH"].ShouldBe("/my/site-packages",
            "PYTHONPATH supplied via extraEnv must be present in EnvironmentVariables");
        psi.EnvironmentVariables["MY_CUSTOM_VAR"].ShouldBe("hello",
            "MY_CUSTOM_VAR supplied via extraEnv must be present in EnvironmentVariables");
    }

    [Fact]
    public void TryBuild_NullExtraEnv_DoesNotThrow()
    {
        var ok = _factory.TryBuild(
            "any-tool",
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: null,
            out var psi,
            out _);

        ok.ShouldBeTrue("TryBuild must succeed even when extraEnv is null");
        psi.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_NullOrWhitespaceCommand_ReturnsFalseWithNonNullError(string? command)
    {
        var ok = _factory.TryBuild(
            command!,
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: null,
            out _,
            out var error);

        ok.ShouldBeFalse(
            "TryBuild must return false for a null/empty/whitespace command");
        error.ShouldNotBeNullOrEmpty(
            "error must contain a human-readable description when TryBuild returns false");
    }

    [Fact]
    public void TryBuild_PathEnvironmentVariableIsAugmented()
    {
        _factory.TryBuild(
            "any-tool",
            Array.Empty<string>(),
            workingDirectory: null,
            extraEnv: null,
            out var psi,
            out _);

        psi.EnvironmentVariables.ContainsKey("PATH").ShouldBeTrue(
            "EnvironmentVariables must contain a PATH key set by AugmentedPath");

        // On macOS the augmented PATH must include Homebrew.
        if (OperatingSystem.IsMacOS())
        {
            // PATH in the built ProcessStartInfo must include /opt/homebrew/bin on macOS.
            var pathValue = psi.EnvironmentVariables["PATH"];
            pathValue.ShouldNotBeNull("PATH environment variable must be set by AugmentedPath");
            pathValue.ShouldContain("/opt/homebrew/bin");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Constructor guard
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessLaunchFactory_Constructor_ThrowsOnNullProber()
    {
        Should.Throw<ArgumentNullException>(() => new ProcessLaunchFactory(null!),
            "Constructor must reject a null prober with ArgumentNullException");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Conda discovery (ExecutablePathProber + PythonInstallPathScanner)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CondaRootDirectories_OnMacOS_IncludesCommonInstallRoots()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var roots = _prober.CondaRootDirectories();

        // The Homebrew-cask miniconda base must be among the probed conda roots on macOS.
        roots.ShouldContain("/opt/homebrew/Caskroom/miniconda/base");
        // A ~/miniconda3-style root must also be probed.
        roots.ShouldContain(r => r.EndsWith("/miniconda3"));
    }

    [Fact]
    public void WellKnownPythonPaths_OnMacOS_IncludesCondaBaseInterpreter()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var paths = _prober.WellKnownPythonPaths();

        // The conda base interpreter must be a well-known python path so discovery probes it.
        paths.ShouldContain("/opt/homebrew/Caskroom/miniconda/base/bin/python3");
    }

    [Fact]
    public void CondaEnvPythonPaths_FindsInterpretersInNamedEnvironments()
    {
        // Arrange – a fake conda root: <tmp>/<guid>/envs/myenv/bin/python3
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var envBin = Path.Combine(root, "envs", "myenv", "bin");
        Directory.CreateDirectory(envBin);
        var py = Path.Combine(envBin, "python3");
        File.WriteAllText(py, string.Empty);
        _tempDirs.Add(root);

        // Act
        var found = PythonInstallPathScanner.CondaEnvPythonPaths(new[] { root });

        // Assert – must discover <root>/envs/<name>/bin/python3.
        found.ShouldContain(py);
    }

    [Fact]
    public void CondaEnvPythonPaths_RootWithoutEnvsDir_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var found = PythonInstallPathScanner.CondaEnvPythonPaths(new[] { root });

        found.ShouldBeEmpty("a conda root without an envs/ directory must yield no interpreters");
    }
}

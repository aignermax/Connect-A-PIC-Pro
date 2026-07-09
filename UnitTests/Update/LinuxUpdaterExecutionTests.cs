using System.Diagnostics;
using CAP.Avalonia.Services.Update;
using Shouldly;

namespace UnitTests.Update;

/// <summary>
/// Runs the generated Linux updater script for real against a sandbox install directory and
/// asserts the data-loss fix (issue #616): the app's own files are replaced, but unrelated
/// files sharing the directory survive, and no backup/stage folder is left behind. bash-only,
/// so it runs on the Linux CI runner and macOS, and is skipped on Windows.
/// </summary>
public class LinuxUpdaterExecutionTests
{
    [Fact]
    public void BuildLinux_Run_ReplacesAppFilesButKeepsUnrelatedFiles()
    {
        if (OperatingSystem.IsWindows()) return;   // no bash

        var sandbox = Path.Combine(Path.GetTempPath(), $"lunima-upd-{Guid.NewGuid():N}");
        var target = Path.Combine(sandbox, "install");
        Directory.CreateDirectory(target);
        try
        {
            const string exeName = "Lunima";
            // Old install: the app executable + a native lib + an UNRELATED user file.
            File.WriteAllText(Path.Combine(target, exeName), "#!/bin/sh\nexit 0\n");   // old version
            File.WriteAllText(Path.Combine(target, "libSkiaSharp.so"), "OLD-LIB");
            File.WriteAllText(Path.Combine(target, "my_notes.txt"), "PRECIOUS USER DATA");
            MakeExecutable(Path.Combine(target, exeName));

            // New release tarball: updated executable + updated lib + a brand-new file. NO notes.
            var stage = Path.Combine(sandbox, "release");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, exeName), "#!/bin/sh\n# NEW\nexit 0\n");
            File.WriteAllText(Path.Combine(stage, "libSkiaSharp.so"), "NEW-LIB");
            File.WriteAllText(Path.Combine(stage, "README.md"), "new in this release");
            var archive = Path.Combine(sandbox, "lunima.tar.gz");
            Run("tar", $"-czf \"{archive}\" -C \"{stage}\" .", sandbox);

            // Dead PID so the wait-for-exit loop returns immediately.
            var target_ = new InstallLocation(target, Path.Combine(target, exeName), DeadPid());
            var script = UpdaterScripts.BuildLinux(target_, archive);
            var scriptPath = Path.Combine(sandbox, "update.sh");
            File.WriteAllText(scriptPath, script);
            MakeExecutable(scriptPath);

            Run("bash", $"\"{scriptPath}\"", sandbox);

            // App files updated…
            File.ReadAllText(Path.Combine(target, exeName)).ShouldContain("# NEW");
            File.ReadAllText(Path.Combine(target, "libSkiaSharp.so")).ShouldBe("NEW-LIB");
            File.Exists(Path.Combine(target, "README.md")).ShouldBeTrue();
            // …unrelated file preserved — the whole point of #616.
            File.ReadAllText(Path.Combine(target, "my_notes.txt")).ShouldBe("PRECIOUS USER DATA");
            // No backup/stage litter left behind.
            Directory.GetFileSystemEntries(target, ".lunima-backup*").ShouldBeEmpty();
            Directory.GetFileSystemEntries(sandbox, ".lunima-update*").ShouldBeEmpty();
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void BuildLinux_Run_MkdirBackupFailure_AbortsWithoutReportingSuccess()
    {
        if (OperatingSystem.IsWindows()) return;   // no bash

        var sandbox = Path.Combine(Path.GetTempPath(), $"lunima-upd-ro-{Guid.NewGuid():N}");
        var target = Path.Combine(sandbox, "install");
        Directory.CreateDirectory(target);
        try
        {
            const string exeName = "Lunima";
            File.WriteAllText(Path.Combine(target, exeName), "#!/bin/sh\n# OLD\nexit 0\n");
            File.WriteAllText(Path.Combine(target, "my_notes.txt"), "PRECIOUS USER DATA");
            MakeExecutable(Path.Combine(target, exeName));

            var stage = Path.Combine(sandbox, "release");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, exeName), "#!/bin/sh\n# NEW\nexit 0\n");
            var archive = Path.Combine(sandbox, "lunima.tar.gz");
            Run("tar", $"-czf \"{archive}\" -C \"{stage}\" .", sandbox);

            var loc = new InstallLocation(target, Path.Combine(target, exeName), DeadPid());
            var scriptPath = Path.Combine(sandbox, "update.sh");
            File.WriteAllText(scriptPath, UpdaterScripts.BuildLinux(loc, archive));
            MakeExecutable(scriptPath);

            // Make the target read-only so the backup mkdir (inside target) fails — the updater
            // must abort cleanly, never printing success and never touching the user's files.
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                RunCapture("bash", $"\"{scriptPath}\"", sandbox);
            }
            finally
            {
                File.SetUnixFileMode(target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var log = File.ReadAllText(Path.Combine(sandbox, "lunima-update.log"));
            log.ShouldNotContain("=== update OK ===");
            // Nothing was replaced; the original files are intact.
            File.ReadAllText(Path.Combine(target, exeName)).ShouldContain("# OLD");
            File.ReadAllText(Path.Combine(target, "my_notes.txt")).ShouldBe("PRECIOUS USER DATA");
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void RunCapture(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.Environment["TMPDIR"] = cwd;   // isolate the script's log to this sandbox
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
    }

    private static int DeadPid()
    {
        // Spawn a trivial process, wait for it to exit, reuse its (now-dead) PID.
        var p = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false });
        p!.WaitForExit();
        return p.Id;
    }

    private static void MakeExecutable(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

    private static void Run(string file, string args, string cwd)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit(30_000);
    }
}

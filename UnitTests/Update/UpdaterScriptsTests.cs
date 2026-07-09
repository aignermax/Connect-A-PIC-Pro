using CAP.Avalonia.Services.Update;
using Shouldly;

namespace UnitTests.Update;

/// <summary>
/// Verifies the generated updater scripts embed the right values and use the safe swap recipe.
/// These are the risky part of the self-updater, so they're pinned with unit tests.
/// </summary>
public class UpdaterScriptsTests
{
    private static InstallLocation MacTarget() => new(
        "/Applications/Lunima.app",
        "/Applications/Lunima.app/Contents/MacOS/CAP.Desktop",
        4242);

    [Fact]
    public void BuildMacOs_UsesDittoDequarantineAdHocSignAtomicSwapAndRelaunch()
    {
        var script = UpdaterScripts.BuildMacOs(MacTarget(), "/tmp/Lunima_Update_abc.zip");

        script.ShouldStartWith("#!/usr/bin/env bash");
        script.ShouldContain("OLD_PID=4242");
        script.ShouldContain("TARGET='/Applications/Lunima.app'");
        script.ShouldContain("ARCHIVE='/tmp/Lunima_Update_abc.zip'");
        script.ShouldContain("ditto -x -k");                    // extract preserving the bundle seal
        script.ShouldContain("xattr -dr com.apple.quarantine"); // no Gatekeeper prompt / translocation
        script.ShouldContain("codesign --force --deep --sign -");   // recursive ad-hoc sign (covers nested .NET code)
        script.ShouldContain("codesign --verify --deep --strict");  // verify the seal BEFORE swapping
        script.ShouldContain("open -n");                        // relaunch a fresh instance
        script.ShouldContain("rollback");                       // has a rollback path
        script.ShouldContain("SWAPPED=1");                      // rollback distinguishes pre/post-swap
        script.ShouldNotContain("cp -R");                       // never cp -R a bundle (corrupts signature)
    }

    [Fact]
    public void BuildMacOs_SingleQuotesPathsWithSpacesAndEmbeddedQuotes()
    {
        var target = new InstallLocation(
            "/Apps/My Lunima.app", "/Apps/My Lunima.app/Contents/MacOS/CAP.Desktop", 7);

        var script = UpdaterScripts.BuildMacOs(target, "/tmp/o'brien.zip");

        script.ShouldContain("TARGET='/Apps/My Lunima.app'");
        script.ShouldContain(@"ARCHIVE='/tmp/o'\''brien.zip'");   // POSIX single-quote escaping
    }

    [Fact]
    public void BuildLinux_ReplacesFilesInPlaceWithoutMovingOrRemovingTargetDir()
    {
        var target = new InstallLocation("/opt/lunima", "/opt/lunima/Lunima", 55);

        var script = UpdaterScripts.BuildLinux(target, "/tmp/lunima.tar.gz");

        script.ShouldStartWith("#!/usr/bin/env bash");
        script.ShouldContain("OLD_PID=55");
        script.ShouldContain("TARGET='/opt/lunima'");
        script.ShouldContain("EXE_NAME='Lunima'");
        script.ShouldContain("tar -xzf");
        script.ShouldContain("rollback");
        // Data-loss guard (#616): the target directory is a possibly-shared folder, so the
        // updater must replace the release's files one by one — never move or remove the whole
        // TARGET directory (that would take unrelated files with it).
        script.ShouldNotContain("mv \"$TARGET\" ");   // no whole-directory move-aside
        script.ShouldNotContain("rm -rf \"$TARGET\""); // no whole-directory delete
        script.ShouldContain("find . -mindepth 1 -maxdepth 1 -print0"); // per-entry replace loop
    }

    [Fact]
    public void BuildLinux_RelaunchGuard_ProbesTheSpawnedPidBeforeDeletingTheBackup()
    {
        var target = new InstallLocation("/opt/lunima", "/opt/lunima/Lunima", 55);

        var script = UpdaterScripts.BuildLinux(target, "/tmp/lunima.tar.gz");

        // A backgrounded subshell always exits 0, so `( … & ) || rollback` could never
        // fire — the guard must probe the spawned PID instead, and only then delete
        // the backup (a non-starting binary rolls back to the known-good files).
        script.ShouldContain("NEW_PID=$!");
        script.ShouldContain("if ! kill -0 \"$NEW_PID\" 2>/dev/null; then");
        script.ShouldContain("wait \"$NEW_PID\" || rollback \"relaunch failed\"");
        script.ShouldNotContain("( \"$TARGET/$EXE_NAME\" >/dev/null 2>&1 & ) ||");
    }

    [Fact]
    public void BuildWindows_WaitsForExitRunsMsiexecAndRelaunches()
    {
        var target = new InstallLocation(
            @"C:\Program Files\Lunima", @"C:\Program Files\Lunima\Lunima.exe", 99);

        var script = UpdaterScripts.BuildWindows(target, @"C:\Temp\Lunima-Setup.msi");

        script.ShouldContain("$targetPid = 99");
        script.ShouldContain("Wait-Process");
        script.ShouldContain("Start-Process msiexec");
        script.ShouldContain(@"$msi = 'C:\Temp\Lunima-Setup.msi'");
        script.ShouldContain(@"$exe = 'C:\Program Files\Lunima\Lunima.exe'");
        script.ShouldContain("Start-Process -FilePath $exe");
    }
}

using System.Globalization;

namespace CAP.Avalonia.Services.Update;

/// <summary>
/// Generates the platform-specific updater scripts that replace the installed app and relaunch.
/// These are pure string builders (no I/O), so the exact commands are unit-testable. The scripts
/// are run detached by <see cref="DetachedUpdaterLauncher"/> after the app quits.
/// </summary>
public static class UpdaterScripts
{
    /// <summary>Builds the macOS updater (bash): ditto-extract → de-quarantine → deep ad-hoc sign → near-atomic swap → relaunch.</summary>
    public static string BuildMacOs(InstallLocation target, string archivePath) =>
        MacTemplate
            .Replace("__OLD_PID__", target.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Replace("__TARGET__", ShQuote(target.Root))
            .Replace("__ARCHIVE__", ShQuote(archivePath))
            .Replace("__EXE_NAME__", ShQuote(Path.GetFileName(target.ExecutablePath)));

    /// <summary>Builds the Linux updater (bash): tar-extract → directory swap → relaunch.</summary>
    public static string BuildLinux(InstallLocation target, string archivePath) =>
        LinuxTemplate
            .Replace("__OLD_PID__", target.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Replace("__TARGET__", ShQuote(target.Root))
            .Replace("__ARCHIVE__", ShQuote(archivePath))
            .Replace("__EXE_NAME__", ShQuote(Path.GetFileName(target.ExecutablePath)));

    /// <summary>Builds the Windows updater (PowerShell): wait for exit → msiexec in-place upgrade → relaunch.</summary>
    public static string BuildWindows(InstallLocation target, string msiPath) =>
        WindowsTemplate
            .Replace("__OLD_PID__", target.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Replace("__MSI__", PsQuote(msiPath))
            .Replace("__EXE__", PsQuote(target.ExecutablePath));

    /// <summary>Single-quotes a value for safe use in a POSIX shell (escapes embedded quotes).</summary>
    internal static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Single-quotes a value for safe use in PowerShell (doubles embedded quotes).</summary>
    internal static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private const string MacTemplate =
        """
        #!/usr/bin/env bash
        # Lunima in-place updater (macOS) — generated. Replaces the running .app and relaunches.
        set -uo pipefail
        OLD_PID=__OLD_PID__
        TARGET=__TARGET__
        ARCHIVE=__ARCHIVE__
        EXE_NAME=__EXE_NAME__
        LOG="${TMPDIR:-/tmp}/lunima-update.log"
        exec >>"$LOG" 2>&1
        echo "=== lunima update $(date) pid=$OLD_PID target=$TARGET ==="
        PARENT="$(dirname "$TARGET")"
        BACKUP="$TARGET.bak.$$"
        STAGEDIR="$PARENT/.lunima-update.$$"
        SWAPPED=0

        rollback() {
          # Only reached after the app has exited (the wait timeout below aborts without calling this).
          echo "ROLLBACK: $1"
          [ -d "$STAGEDIR" ] && rm -rf "$STAGEDIR"
          if [ "$SWAPPED" = "1" ]; then
            # Swap already happened: move the failed new bundle aside and restore the known-good backup.
            [ -e "$TARGET" ] && mv "$TARGET" "$TARGET.failed.$$" 2>/dev/null
            [ -d "$BACKUP" ] && mv "$BACKUP" "$TARGET"
          fi
          # Pre-swap failures leave TARGET as the original bundle; relaunch it so a working app survives.
          open -n "$TARGET" 2>/dev/null || true
          exit 1
        }

        # 1. Wait for the running app to exit (~30s). If it never exits, abort WITHOUT relaunching —
        #    the original is still running, so there is nothing to recover and a 2nd instance would clash.
        for _ in $(seq 1 60); do kill -0 "$OLD_PID" 2>/dev/null || break; sleep 0.5; done
        if kill -0 "$OLD_PID" 2>/dev/null; then echo "app did not exit; aborting"; exit 1; fi

        # 2. Extract the new bundle beside the target (same volume -> the final mv is atomic).
        mkdir -p "$STAGEDIR" || rollback "mkdir stage failed"
        ditto -x -k "$ARCHIVE" "$STAGEDIR" || rollback "extract failed"
        NEW="$(find "$STAGEDIR" -maxdepth 2 -name '*.app' -type d | head -1)"
        [ -n "$NEW" ] || rollback "no .app found in archive"

        # 3. De-quarantine (prevents the Gatekeeper prompt AND App Translocation); verify it cleared.
        xattr -dr com.apple.quarantine "$NEW" 2>/dev/null || true
        xattr -pr com.apple.quarantine "$NEW" 2>/dev/null | grep -q . && rollback "quarantine not cleared"

        # 4. Ad-hoc sign the WHOLE bundle recursively (Apple Silicon requires >= ad-hoc). --deep is
        #    required: a .NET self-contained bundle carries managed .dll + bare Mach-O helpers
        #    (e.g. createdump) that a top-level-only seal refuses to cover. Verify BEFORE swapping so
        #    a bad seal is caught while the old bundle is still in place.
        codesign --force --deep --sign - "$NEW" || rollback "codesign failed"
        codesign --verify --deep --strict "$NEW" || rollback "signature verify failed"

        # 5. Near-atomic swap: move old aside, new into place. Set SWAPPED as soon as the old
        #    bundle has moved — if the SECOND mv fails, TARGET no longer exists, and the rollback
        #    must restore from BACKUP (which the SWAPPED branch does). Setting it only after both
        #    moves would strand the working app at BACKUP with TARGET gone.
        mv "$TARGET" "$BACKUP" || rollback "could not move old bundle aside"
        SWAPPED=1
        mv "$NEW" "$TARGET" || rollback "could not move new bundle into place"

        # 6. Relaunch the new version, then clean up.
        open -n "$TARGET" || rollback "relaunch failed"
        [ -d "$STAGEDIR" ] && rm -rf "$STAGEDIR"
        rm -rf "$BACKUP"
        rm -f "$ARCHIVE" 2>/dev/null || true
        echo "=== update OK ==="
        """;

    // The Linux build is a portable single-file binary, so TARGET is wherever the user put it —
    // possibly a folder that also holds unrelated files. The updater therefore replaces ONLY the
    // files the new release ships (the entries under NEWDIR), file by file, and never moves or
    // removes the TARGET directory itself. Any file in TARGET that is not part of the release is
    // left untouched; the backup holds only the app files we replaced, so cleaning it up can
    // never delete the user's data (issue #616).
    private const string LinuxTemplate =
        """
        #!/usr/bin/env bash
        # Lunima in-place updater (Linux) — generated. Replaces the app's files in place and relaunches.
        set -uo pipefail
        OLD_PID=__OLD_PID__
        TARGET=__TARGET__
        ARCHIVE=__ARCHIVE__
        EXE_NAME=__EXE_NAME__
        LOG="${TMPDIR:-/tmp}/lunima-update.log"
        exec >>"$LOG" 2>&1
        echo "=== lunima update $(date) pid=$OLD_PID target=$TARGET ==="
        PARENT="$(dirname "$TARGET")"
        BACKUP="$TARGET/.lunima-backup.$$"
        STAGEROOT="$PARENT/.lunima-update.$$"

        cleanup_stage() { [ -d "$STAGEROOT" ] && rm -rf "$STAGEROOT"; }

        rollback() {
          echo "ROLLBACK: $1"
          # Restore every file we backed up over whatever partial install is in place. Use process
          # substitution (not a pipe) so this loop runs in THIS shell, and track failures so a file
          # that cannot be restored is not then deleted with the backup (it stays recoverable).
          restore_failed=0
          if [ -d "$BACKUP" ]; then
            while IFS= read -r -d '' n; do
              n="${n#./}"
              rm -rf "$TARGET/$n" 2>/dev/null
              if ! mv "$BACKUP/$n" "$TARGET/$n" 2>/dev/null; then
                echo "WARN: could not restore $n; kept in $BACKUP"
                restore_failed=1
              fi
            done < <(cd "$BACKUP" && find . -mindepth 1 -maxdepth 1 -print0)
            if [ "$restore_failed" = "0" ]; then rm -rf "$BACKUP"; else echo "backup kept at $BACKUP"; fi
          fi
          cleanup_stage
          [ -x "$TARGET/$EXE_NAME" ] && ( "$TARGET/$EXE_NAME" >/dev/null 2>&1 & )
          exit 1
        }

        # Wait for exit; if the app never exits, abort without relaunching (avoids a 2nd instance).
        for _ in $(seq 1 60); do kill -0 "$OLD_PID" 2>/dev/null || break; sleep 0.5; done
        if kill -0 "$OLD_PID" 2>/dev/null; then echo "app did not exit; aborting"; exit 1; fi

        mkdir -p "$STAGEROOT" || { echo "mkdir stage failed"; exit 1; }
        tar -xzf "$ARCHIVE" -C "$STAGEROOT" || { cleanup_stage; echo "extract failed"; exit 1; }
        # The tarball may hold files at its root or inside one folder; locate the executable.
        NEWDIR="$STAGEROOT"
        if [ ! -f "$STAGEROOT/$EXE_NAME" ]; then
          SUB="$(find "$STAGEROOT" -maxdepth 2 -type f -name "$EXE_NAME" | head -1)"
          [ -n "$SUB" ] && NEWDIR="$(dirname "$SUB")"
        fi
        [ -f "$NEWDIR/$EXE_NAME" ] || { cleanup_stage; echo "executable not found in archive"; exit 1; }

        # Replace the release's files one by one; back up only what we overwrite. Process
        # substitution (not a pipe) keeps this loop in THIS shell — a piped `while` runs in a
        # subshell, where rollback's `exit` would only leave the subshell and the script would
        # then fall through to the success path (relaunching twice, printing "update OK").
        mkdir -p "$BACKUP" || { cleanup_stage; echo "mkdir backup failed"; exit 1; }
        while IFS= read -r -d '' n; do
          n="${n#./}"
          if [ -e "$TARGET/$n" ]; then
            mv "$TARGET/$n" "$BACKUP/$n" || rollback "backup of $n failed"
          fi
          mv "$NEWDIR/$n" "$TARGET/$n" || rollback "install of $n failed"
        done < <(cd "$NEWDIR" && find . -mindepth 1 -maxdepth 1 -print0)
        chmod +x "$TARGET/$EXE_NAME" 2>/dev/null || true

        # Relaunch the new binary and verify it comes up before the backup is deleted.
        # A backgrounded subshell always exits 0, so the previous `( … & ) || rollback`
        # guard could never fire — probe the spawned PID instead. Still running after a
        # second (the normal GUI case) or exited cleanly counts as up; a failed exec or
        # early crash (missing libs, wrong arch, truncated binary) rolls back.
        "$TARGET/$EXE_NAME" >/dev/null 2>&1 &
        NEW_PID=$!
        sleep 1
        if ! kill -0 "$NEW_PID" 2>/dev/null; then
          wait "$NEW_PID" || rollback "relaunch failed"
        fi
        rm -rf "$BACKUP"
        cleanup_stage
        rm -f "$ARCHIVE" 2>/dev/null || true
        echo "=== update OK ==="
        """;

    private const string WindowsTemplate =
        """
        # Lunima in-place updater (Windows) — generated. Runs the MSI upgrade and relaunches.
        $ErrorActionPreference = 'Continue'
        $targetPid = __OLD_PID__
        $msi = __MSI__
        $exe = __EXE__
        $log = Join-Path $env:TEMP 'lunima-update.log'
        "=== lunima update $(Get-Date) pid=$targetPid ===" | Out-File -FilePath $log -Append
        try { Wait-Process -Id $targetPid -Timeout 60 -ErrorAction SilentlyContinue } catch {}
        $p = Start-Process msiexec -ArgumentList '/i', $msi, '/qb' -Wait -PassThru
        "msiexec exit $($p.ExitCode)" | Out-File -FilePath $log -Append
        Start-Process -FilePath $exe
        """;
}

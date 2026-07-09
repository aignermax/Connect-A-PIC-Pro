using System.Diagnostics;

namespace CAP.Avalonia.Services.Update;

/// <summary>
/// Launches a generated updater script as a DETACHED process that outlives the quitting app.
/// This is the single place in the update feature that constructs a <see cref="ProcessStartInfo"/>
/// directly (see <c>CrossPlatformProcessLaunchTests.AllowedDirectLaunchFiles</c>): the updater must
/// survive its parent exiting, so it can't be an awaited <c>ProcessLaunchFactory</c> tool launch.
/// </summary>
public class DetachedUpdaterLauncher
{
    /// <summary>
    /// Starts <paramref name="scriptPath"/> detached from the current process. On Windows it runs
    /// via a hidden PowerShell; on macOS/Linux via <c>nohup bash … &amp;</c> so it keeps running
    /// after the app quits. Marked <c>virtual</c> so tests can substitute a no-op launcher.
    /// </summary>
    public virtual void Launch(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            Process.Start(psi);
            return;
        }

        // macOS/Linux: nohup + background so the helper is not killed when the app exits. Resolve
        // bash via env (portable across /bin/bash vs /usr/bin/bash) instead of hardcoding a path.
        var shell = new ProcessStartInfo
        {
            FileName = "/usr/bin/env",
            UseShellExecute = false,
        };
        shell.ArgumentList.Add("bash");
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add($"nohup bash {Quote(scriptPath)} >/dev/null 2>&1 &");
        Process.Start(shell);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

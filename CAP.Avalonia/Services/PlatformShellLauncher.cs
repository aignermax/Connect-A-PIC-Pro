using System.Diagnostics;
using System.IO;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// Launches URLs and file-system paths via the correct OS shell command.
/// On macOS uses <c>open</c>, on Windows uses <c>UseShellExecute</c>, on Linux uses <c>xdg-open</c>.
/// </summary>
public sealed class PlatformShellLauncher : IUrlLauncher
{
    // ─── Named command constants ──────────────────────────────────────────────

    private const string OpenCommand   = "open";
    private const string XdgOpen      = "xdg-open";
    private const string ExplorerExe  = "explorer.exe";
    private const string RevealFlag   = "-R";
    private const string SelectFlag   = "/select,";

    private readonly ProcessLaunchFactory _launchFactory;

    /// <summary>
    /// Initializes a new <see cref="PlatformShellLauncher"/>.
    /// </summary>
    /// <param name="launchFactory">Factory used to build process start infos.</param>
    public PlatformShellLauncher(ProcessLaunchFactory launchFactory)
    {
        _launchFactory = launchFactory ?? throw new ArgumentNullException(nameof(launchFactory));
    }

    /// <summary>
    /// Creates a launcher backed by a default <see cref="ProcessLaunchFactory"/>. Use when a
    /// caller (a ViewModel constructed outside DI, or a test) has no launcher to inject;
    /// production code resolves the shared <see cref="IUrlLauncher"/> singleton instead.
    /// </summary>
    public static PlatformShellLauncher CreateDefault() => new PlatformShellLauncher(ProcessLaunchFactory.CreateDefault());

    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    public void Open(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return;
        }

        var command = OperatingSystem.IsMacOS() ? OpenCommand : XdgOpen;
        if (!_launchFactory.TryBuild(command, new[] { url }, null, null, out var psi, out _))
            return;
        Process.Start(psi);
    }

    /// <summary>Opens <paramref name="path"/> with the default application for its type.</summary>
    public void OpenFileOrDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return;
        }

        var command = OperatingSystem.IsMacOS() ? OpenCommand : XdgOpen;
        if (!_launchFactory.TryBuild(command, new[] { path }, null, null, out var psi, out _))
            return;
        Process.Start(psi);
    }

    /// <summary>Reveals <paramref name="path"/> in the system file manager.</summary>
    public void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (!_launchFactory.TryBuild(OpenCommand, new[] { RevealFlag, path }, null, null, out var psi, out _))
                return;
            Process.Start(psi);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // explorer.exe is finicky about /select, tokenization: passed via ArgumentList,
            // .NET quotes the whole "/select,<path>" token, which explorer mis-parses when the
            // path contains spaces. The historically-reliable form is a single raw argument
            // string with only the path quoted: /select,"C:\full path\file".
            Process.Start(new ProcessStartInfo
            {
                FileName = ExplorerExe,
                Arguments = $"{SelectFlag}\"{path}\"",
                UseShellExecute = false,
            });
            return;
        }

        // Linux: open the parent directory
        var dir = Path.GetDirectoryName(path) ?? path;
        if (!_launchFactory.TryBuild(XdgOpen, new[] { dir }, null, null, out var linuxPsi, out _))
            return;
        Process.Start(linuxPsi);
    }
}

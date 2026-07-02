namespace CAP.Avalonia.Services;

/// <summary>
/// Opens URLs and file-system paths using the host operating system.
/// Abstracted so tests can verify behavior without launching a real browser or file manager.
/// </summary>
public interface IUrlLauncher
{
    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    void Open(string url);

    /// <summary>Opens <paramref name="path"/> with the default application for its type.</summary>
    void OpenFileOrDirectory(string path);

    /// <summary>Reveals <paramref name="path"/> in the system file manager (Finder, Explorer, etc.).</summary>
    void RevealInFileManager(string path);
}

namespace UnitTests.Export.CornerstoneDrc;

/// <summary>
/// Repo-relative locations of the vendored CORNERSTONE DRC deck and its runner script.
/// Same repo-root walk as the architecture tests (works in clones and git worktrees).
/// </summary>
internal static class CornerstoneDrcPaths
{
    public static string DeckFile =>
        Path.Combine(FindRepoRoot(), "scripts", "drc", "cornerstone_sin300_drc.lydrc");

    public static string DeckFolder =>
        Path.Combine(FindRepoRoot(), "scripts", "drc");

    public static string RunnerScript =>
        Path.Combine(FindRepoRoot(), "scripts", "run_cornerstone_drc.py");

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root (.git directory or file).");
    }
}

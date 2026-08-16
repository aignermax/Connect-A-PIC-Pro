using Shouldly;

namespace UnitTests.Architecture;

/// <summary>
/// Guards that the shipped example designs (<c>examples/*.lun</c>) are bundled
/// into every release artifact. The app discovers them by walking up from its
/// base directory (<see cref="CAP.Avalonia.Services.ExampleDesignsService"/>),
/// so each packaging path must place an <c>examples/</c> folder beside the
/// executable — otherwise the Home screen's Examples section is silently empty
/// in installed builds (issue #768).
/// </summary>
public class ExamplesPackagingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void RepositoryExamplesDirectory_ContainsAtLeastOneDesign()
    {
        var examplesDir = Path.Combine(RepoRoot, "examples");

        Directory.Exists(examplesDir).ShouldBeTrue(
            "examples/ is missing — the release bundles would ship an empty Examples section.");
        Directory.GetFiles(examplesDir, "*.lun").ShouldNotBeEmpty(
            "examples/ contains no .lun designs — the release bundles would ship an empty Examples section.");
    }

    [Fact]
    public void PortableBuildJob_CopiesExamplesIntoPublishOutput()
    {
        var workflow = ReadWorkflow();

        workflow.Contains("cp -r examples publish/examples").ShouldBeTrue(
            "The portable win-x64/linux-x64 artifacts must contain examples/ beside the binary.");
    }

    [Fact]
    public void MsiBuildJob_CopiesExamplesIntoPublishOutput_BeforeHarvesting()
    {
        var workflow = ReadWorkflow();

        var copyIndex = workflow.IndexOf("cp -r examples publish/win-x64-msi/examples", StringComparison.Ordinal);
        copyIndex.ShouldBeGreaterThanOrEqualTo(0,
            "The MSI job must copy examples/ into publish/win-x64-msi so harvesting picks it up.");

        var harvestIndex = workflow.IndexOf("Generate-HarvestedFiles.ps1", StringComparison.Ordinal);
        harvestIndex.ShouldBeGreaterThan(copyIndex,
            "examples/ must be copied before Generate-HarvestedFiles.ps1 runs, otherwise the MSI misses them.");
    }

    [Fact]
    public void MacOsBundleScript_CopiesExamplesBesideTheExecutable()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "build_macos_bundle.sh"));

        script.Contains("\"${MACOS_DIR}/examples\"").ShouldBeTrue(
            "The .app bundle must contain examples/ in Contents/MacOS (the app base directory).");
    }

    private static string ReadWorkflow()
    {
        return File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "Build_Exe.yaml"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            // In a normal clone .git is a directory; in a git worktree it is a file.
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root (.git directory or file).");
    }
}

using System.IO;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Pins the contract that the bundled PDK JSON files match what
/// <see cref="PdkJsonSaver"/> would emit for them. Without this guard the
/// editor's first save churns every component (key reorder, null strip,
/// integer-valued double formatting) and pollutes git history with
/// non-substantive line changes.
/// </summary>
public class PdkJsonSaverRoundTripTests
{
    public static TheoryData<string> BundledPdkPaths()
    {
        var data = new TheoryData<string>();
        var repoRoot = FindRepoRoot();
        foreach (var path in Directory.GetFiles(
                     Path.Combine(repoRoot, "CAP-DataAccess", "PDKs"), "*.json"))
        {
            data.Add(path);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BundledPdkPaths))]
    public void BundledPdkFile_RoundTripsThroughSaverWithoutChanges(string path)
    {
        var loader = new PdkLoader();
        var saver = new PdkJsonSaver();

        var pdk = loader.LoadFromFileForEditing(path);

        // gdsfactory-backend PDKs are machine-generated (scripts/generate_*), not hand-edited,
        // so byte-exact round-trip identity is not a maintenance guarantee for them (issue #570).
        if (pdk.IsGdsFactoryBackend)
            return;

        // Stable path so failures can be diffed manually with the source.
        // Left on disk on failure for inspection; removed on success.
        var name = Path.GetFileNameWithoutExtension(path);
        var tmp = Path.Combine(Path.GetTempPath(), $"pdkroundtrip_{name}.json");
        saver.SaveToFile(pdk, tmp);
        var savedText  = File.ReadAllText(tmp);
        var sourceText = File.ReadAllText(path);

        // Normalize line endings — the saver writes \n through
        // Encoding.UTF8 + WriteAllText; the repo files may have \r\n on
        // Windows. The contract is logical content (key order, value
        // formatting), not EOL style.
        string Norm(string s) => s.Replace("\r\n", "\n");

        Norm(savedText).ShouldBe(Norm(sourceText),
            $"Saver output does not match source — see {tmp} and " +
            $"re-run scripts/normalize_pdk.py against {path}");

        // Clean up only on success (we already passed ShouldBe).
        File.Delete(tmp);
    }

    [Fact]
    public void GdsFactoryBackendPdk_PreservesBackendAndGdsFactoryFunctionThroughSaver()
    {
        // The byte-exact guard above skips gdsfactory-backend PDKs (machine-generated), so this
        // guards the semantic contract instead: the saver must not drop/mangle the new
        // `backend` / `gdsFactoryFunction` fields, or a first-save in the editor would demote
        // the SiN PDK back to a nazca-backend PDK (#570 review).
        var path = Path.Combine(FindRepoRoot(), "CAP-DataAccess", "PDKs", "cornerstone-sin-pdk.json");
        var loader = new PdkLoader();
        var saver = new PdkJsonSaver();
        var original = loader.LoadFromFileForEditing(path);
        original.IsGdsFactoryBackend.ShouldBeTrue();
        original.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.GdsFactoryFunction));

        var tmp = Path.Combine(Path.GetTempPath(), $"pdkroundtrip_semantic_{System.Guid.NewGuid():N}.json");
        try
        {
            saver.SaveToFile(original, tmp);
            var reloaded = loader.LoadFromFileForEditing(tmp);

            reloaded.IsGdsFactoryBackend.ShouldBeTrue();
            reloaded.Components.Count.ShouldBe(original.Components.Count);
            reloaded.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.GdsFactoryFunction));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string FindRepoRoot()
    {
        // `.git` is a directory in a normal checkout but a *file* in a git worktree.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new System.InvalidOperationException("Could not find repo root.");
    }
}

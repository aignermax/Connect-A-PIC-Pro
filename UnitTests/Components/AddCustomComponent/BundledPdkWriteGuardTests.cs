using System;
using System.Collections.Generic;
using System.IO;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Round-5 defense in depth (review finding [1]): bundled foundry PDK JSONs are
/// read-only at runtime. <see cref="BundledPdkPaths"/> classifies bundled paths
/// independently of the library's registration state, and
/// <see cref="PdkJsonSaver.SaveToFile"/> refuses any write target inside the
/// bundled directory — even when a caller lost track of the file's origin.
/// </summary>
public class BundledPdkWriteGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lunima-writeguard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static PdkDraft MinimalDraft() => new()
    {
        Name = "Guard PDK",
        Components = new List<PdkComponentDraft>()
    };

    [Fact]
    public void IsBundledPdkFile_TrueForShippedPdksFolderNextToExecutable()
    {
        var baseDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(baseDir, "PDKs"));

        BundledPdkPaths.IsBundledPdkFile(
            Path.Combine(baseDir, "PDKs", "siepic.json"), baseDir).ShouldBeTrue();
    }

    [Fact]
    public void IsBundledPdkFile_TrueForRepoSourcePdksAboveBaseDir()
    {
        // Mirrors a dev run: bin dir sits below the repo root that holds CAP-DataAccess/PDKs.
        var repoPdks = Path.Combine(_root, "CAP-DataAccess", "PDKs");
        Directory.CreateDirectory(repoPdks);
        var baseDir = Path.Combine(_root, "App", "bin", "Debug");
        Directory.CreateDirectory(baseDir);

        BundledPdkPaths.IsBundledPdkFile(
            Path.Combine(repoPdks, "siepic.json"), baseDir).ShouldBeTrue();
    }

    [Fact]
    public void IsBundledPdkFile_FalseForUserPdkLocations()
    {
        var baseDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(baseDir);
        var userPdk = Path.Combine(_root, "user-pdks", "my-fork.json");

        BundledPdkPaths.IsBundledPdkFile(userPdk, baseDir).ShouldBeFalse();
    }

    [Fact]
    public void SaveToFile_IntoBundledDirectory_ThrowsAndWritesNothing()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "shipped.json");
        var saver = new PdkJsonSaver(isBundledPdkPath: _ => true);

        Should.Throw<UnauthorizedAccessException>(() => saver.SaveToFile(MinimalDraft(), target));

        File.Exists(target).ShouldBeFalse("the guard must refuse BEFORE anything is written");
        File.Exists(target + ".tmp").ShouldBeFalse("not even the temp file may be created");
    }

    [Fact]
    public void SaveToFile_DefaultGuard_RefusesTheRealBundledDirectory()
    {
        // The default probe walks up from the test bin dir and finds the repo's
        // CAP-DataAccess/PDKs — the actual shipped foundry files. The write must
        // throw before touching anything, so no artifact is created in the repo.
        var repoRoot = FindRepoRoot();
        var target = Path.Combine(repoRoot, "CAP-DataAccess", "PDKs", "write-guard-proof.json");

        Should.Throw<UnauthorizedAccessException>(() => new PdkJsonSaver().SaveToFile(MinimalDraft(), target));

        File.Exists(target).ShouldBeFalse();
    }

    [Fact]
    public void SaveToFile_UserPdkTarget_StillSaves()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "my-fork.json");

        new PdkJsonSaver().SaveToFile(MinimalDraft(), target);

        File.Exists(target).ShouldBeTrue();
    }

    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git"))
                           && !File.Exists(Path.Combine(dir, ".git")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }
}

using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Covers <c>DesignScopedGdsComponentService.MigrateLegacyImportPdks</c>
/// (issue #830): designs saved before design-scoping reference global
/// <c>gds-import-*</c> user PDKs — on first open those are converted into
/// design-scoped sets (token-form drafts, embedded .gds bytes) without ever
/// deleting the legacy files.
/// </summary>
public class DesignScopedGdsMigrationTests : IDisposable
{
    private const string PdkName = "GDS Import - chip";
    private static readonly byte[] GdsBytes = { 9, 8, 7, 6 };

    private readonly string _userPdkRoot = Path.Combine(
        Path.GetTempPath(), "lunima-test-legacy-pdks-" + Guid.NewGuid().ToString("N"));

    private readonly UserPdkStore _store;
    private readonly GdsDesignScopeTestHost _host;

    public DesignScopedGdsMigrationTests()
    {
        _store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        _host = new GdsDesignScopeTestHost(_store, new PdkLoader());
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_userPdkRoot))
            Directory.Delete(_userPdkRoot, true);
    }

    /// <summary>
    /// Writes a legacy global import PDK the way pre-#830 imports did: the
    /// absolute .gds sidecar path baked into the drafts' raw code, the sidecar
    /// next to the PDK json inside the managed root.
    /// </summary>
    private string SeedLegacyImportPdk(string pdkName, byte[]? gdsBytes = null)
    {
        Directory.CreateDirectory(_userPdkRoot);
        var gdsPath = Path.Combine(_userPdkRoot, "chip.gds");
        File.WriteAllBytes(gdsPath, gdsBytes ?? GdsBytes);

        var escaped = gdsPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var pdkPath = _store.ResolveNamedPath(pdkName);
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = pdkName,
            Backend = "nazca",
            ProcessAgnostic = true,
            Components = new List<PdkComponentDraft>
            {
                new()
                {
                    Name = "wg1",
                    Category = "Custom",
                    WidthMicrometers = 10,
                    HeightMicrometers = 2,
                    RawCode = $"cell = nd.load_gds(filename = \"{escaped}\", cellname = \"wg1\")",
                    Pins = new List<PhysicalPinDraft>
                    {
                        new() { Name = "in0", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
                        new() { Name = "out0", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
                    },
                },
            },
        }, pdkPath);
        return pdkPath;
    }

    [Fact]
    public void Migrate_ConvertsReferencedLegacyPdk_ToTokenFormDesignScopedSet()
    {
        var pdkPath = SeedLegacyImportPdk(PdkName);

        var migrated = _host.Scope.MigrateLegacyImportPdks(new[] { PdkName, "Bundled PDK" });

        migrated.ShouldBe(1);
        var set = _host.Scope.Sets.ShouldHaveSingleItem();
        set.PdkName.ShouldBe(PdkName);
        set.GdsFileName.ShouldBe("chip.gds");
        set.GdsBytes.ShouldBe(GdsBytes);
        set.Drafts.Single().RawCode.ShouldContain(GdsHierarchyImporter.GdsFileNameToken);
        // Migration must never delete the legacy files: another unmigrated
        // design may still reference them.
        File.Exists(pdkPath).ShouldBeTrue();
        File.Exists(Path.Combine(_userPdkRoot, "chip.gds")).ShouldBeTrue();
        // And the migrated set is live in the library right away.
        _host.Templates.ShouldHaveSingleItem().PdkSource.ShouldBe(PdkName);
    }

    [Fact]
    public void Migrate_IgnoresNonImportSources_AndSetsAlreadyInScope()
    {
        SeedLegacyImportPdk(PdkName);
        _host.Scope.MigrateLegacyImportPdks(new[] { PdkName }).ShouldBe(1);

        // Second load of the same design: already in scope, nothing to do.
        _host.Scope.MigrateLegacyImportPdks(new[] { PdkName, "Some Bundled PDK" }).ShouldBe(0);
        _host.Scope.Sets.ShouldHaveSingleItem();
    }

    [Fact]
    public void Migrate_MissingLegacyFile_WarnsAndSkips()
    {
        var warnings = new List<string>();

        _host.Scope.MigrateLegacyImportPdks(new[] { PdkName }, warnings.Add).ShouldBe(0);

        _host.Scope.Sets.ShouldBeEmpty();
        warnings.ShouldHaveSingleItem().ShouldContain(PdkName);
    }

    [Fact]
    public void Migrate_MissingGdsSidecar_WarnsAndSkipsWholeSet()
    {
        SeedLegacyImportPdk(PdkName);
        File.Delete(Path.Combine(_userPdkRoot, "chip.gds"));
        var warnings = new List<string>();

        _host.Scope.MigrateLegacyImportPdks(new[] { PdkName }, warnings.Add).ShouldBe(0);

        _host.Scope.Sets.ShouldBeEmpty();
        warnings.ShouldHaveSingleItem().ShouldContain("chip.gds");
    }

    [Fact]
    public void Migrate_PreservesExactPdkName_SoPlacementsKeepResolving()
    {
        // Placements reference the PdkSource string verbatim — a migrated set
        // must keep it byte-for-byte, including casing and spaces.
        const string quirkyName = "GDS Import - My Chip V2";
        SeedLegacyImportPdk(quirkyName);

        _host.Scope.MigrateLegacyImportPdks(new[] { quirkyName }).ShouldBe(1);

        _host.Scope.Sets.ShouldHaveSingleItem().PdkName.ShouldBe(quirkyName);
    }
}

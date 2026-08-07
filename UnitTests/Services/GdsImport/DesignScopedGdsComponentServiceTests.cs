using CAP.Avalonia.Services.GdsImport.DesignScope;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Covers the design-scope lifecycle of <see cref="DesignScopedGdsComponentService"/>
/// (issue #830): add/register, save-capture, load-restore, clear, and the
/// content-addressed .gds cache — imported components live in the .lun file
/// and never leak into other designs.
/// </summary>
public class DesignScopedGdsComponentServiceTests : IDisposable
{
    private static readonly byte[] GdsBytes = { 1, 2, 3, 4, 5 };

    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static DesignScopedGdsSet Set(string pdkName, byte[]? gdsBytes = null) => new()
    {
        PdkName = pdkName,
        GdsFileName = "chip.gds",
        GdsBytes = gdsBytes ?? GdsBytes,
        Drafts = new List<PdkComponentDraft>
        {
            new()
            {
                Name = "wg1",
                Category = "Custom",
                WidthMicrometers = 10,
                HeightMicrometers = 2,
                RawCode = $"cell = nd.load_gds(filename = \"{GdsHierarchyImporter.GdsFileNameToken}\", cellname = \"wg1\")",
            },
        },
    };

    [Fact]
    public void AddAndRegister_RegistersCachePathCopies_ButStoresTokenFormDrafts()
    {
        _host.Scope.AddAndRegister(Set("GDS Import - chip"));

        var stored = _host.Scope.Sets.ShouldHaveSingleItem();
        stored.Drafts.Single().RawCode.ShouldContain(GdsHierarchyImporter.GdsFileNameToken,
            customMessage: "stored drafts stay portable");

        var registered = _host.LoadedDrafts.ShouldHaveSingleItem().Components.ShouldHaveSingleItem();
        registered.RawCode.ShouldNotContain(GdsHierarchyImporter.GdsFileNameToken);
        registered.RawCode.ShouldContain(_host.GdsCacheDirectory);
        registered.ShouldNotBeSameAs(stored.Drafts.Single(),
            "registration copies must never alias the stored drafts");
    }

    [Fact]
    public void MaterializeGds_IsIdempotent_SameBytesReuseOneCacheFile()
    {
        var first = _host.Scope.MaterializeGds(GdsBytes);
        var second = _host.Scope.MaterializeGds(GdsBytes);

        second.ShouldBe(first);
        File.ReadAllBytes(first).ShouldBe(GdsBytes);
        Directory.GetFiles(_host.GdsCacheDirectory).ShouldHaveSingleItem();
    }

    [Fact]
    public void ResolveAvailablePdkName_SuffixesTakenNamesDeterministically()
    {
        _host.Scope.ResolveAvailablePdkName("GDS Import - chip").ShouldBe("GDS Import - chip");
        _host.Scope.AddAndRegister(Set("GDS Import - chip"));
        _host.Scope.ResolveAvailablePdkName("GDS Import - chip").ShouldBe("GDS Import - chip-2");
        _host.Scope.AddAndRegister(Set("GDS Import - chip-2"));
        _host.Scope.ResolveAvailablePdkName("GDS Import - chip").ShouldBe("GDS Import - chip-3");
    }

    [Fact]
    public void CaptureForSave_ReturnsNullWhenNoImports_SoUntouchedDesignsStayLean()
    {
        _host.Scope.CaptureForSave().ShouldBeNull();
    }

    [Fact]
    public void CaptureForSave_ThenRestoreOnFreshService_RoundTripsSetsAndReregisters()
    {
        _host.Scope.AddAndRegister(Set("GDS Import - chip"));
        var payload = _host.Scope.CaptureForSave();
        payload.ShouldNotBeNull();

        using var reopened = new GdsDesignScopeTestHost();
        reopened.Scope.RestoreDesignScope(payload);

        var restored = reopened.Scope.Sets.ShouldHaveSingleItem();
        restored.PdkName.ShouldBe("GDS Import - chip");
        restored.GdsFileName.ShouldBe("chip.gds");
        restored.GdsBytes.ShouldBe(GdsBytes);
        restored.Drafts.Single().RawCode.ShouldContain(GdsHierarchyImporter.GdsFileNameToken);
        reopened.PdkManager.LoadedPdks.ShouldHaveSingleItem().FilePath.ShouldBeNull();
        reopened.Templates.ShouldHaveSingleItem().Name.ShouldBe("wg1");
    }

    [Fact]
    public void ClearDesignScope_RemovesEverySetFromLibraryAndForgetsThem()
    {
        _host.Scope.AddAndRegister(Set("GDS Import - a"));
        _host.Scope.AddAndRegister(Set("GDS Import - b"));

        _host.Scope.ClearDesignScope();

        _host.Scope.Sets.ShouldBeEmpty();
        _host.Templates.ShouldBeEmpty();
        _host.PdkManager.LoadedPdks.ShouldBeEmpty();
    }

    [Fact]
    public void RestoreDesignScope_ReplacesThePreviousDesignsScope()
    {
        _host.Scope.AddAndRegister(Set("GDS Import - old"));
        var payload = new GdsDesignScopeTestHost();
        payload.Scope.AddAndRegister(Set("GDS Import - new"));

        _host.Scope.RestoreDesignScope(payload.Scope.CaptureForSave());

        _host.Scope.Sets.ShouldHaveSingleItem().PdkName.ShouldBe("GDS Import - new");
        _host.Templates.ShouldAllBe(t => t.PdkSource == "GDS Import - new");
        payload.Dispose();
    }

    [Fact]
    public void RestoreDesignScope_CorruptBase64SetIsSkippedWithWarning_RestSurvives()
    {
        var good = _host.Scope;
        good.AddAndRegister(Set("GDS Import - good"));
        var payload = good.CaptureForSave()!;
        payload.Insert(0, new ImportedGdsComponentSetData
        {
            PdkName = "GDS Import - broken",
            GdsFileName = "broken.gds",
            GdsBase64 = "not-base64!!",
            Components = new List<PdkComponentDraft>(),
        });

        using var reopened = new GdsDesignScopeTestHost();
        var warnings = new List<string>();
        reopened.Scope.RestoreDesignScope(payload, warnings.Add);

        reopened.Scope.Sets.ShouldHaveSingleItem().PdkName.ShouldBe("GDS Import - good");
        warnings.ShouldHaveSingleItem().ShouldContain("GDS Import - broken");
    }
}

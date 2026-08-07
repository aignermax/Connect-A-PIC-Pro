using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsTemplateResolver"/>: known-component resolution from
/// the loaded component library, including the hash-suffix-stripped retry the
/// hierarchy importer performs with this resolver.
/// </summary>
public class GdsTemplateResolverTests
{
    private const double Tolerance = 1e-9;

    private static ComponentTemplate MmiTemplate() => new()
    {
        Name = "mmi1x2",
        PdkSource = "testpdk",
        WidthMicrometers = 30,
        HeightMicrometers = 10,
        PinDefinitions = new[]
        {
            new PinDefinition("o1", 0, 5, 180),
            new PinDefinition("o2", 30, 5, 0),
        },
    };

    [Fact]
    public void Resolver_ExactTemplateName_ResolvesWithTemplatePinsInAppSpace()
    {
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { MmiTemplate() });

        var known = resolver("mmi1x2");

        known.ShouldNotBeNull();
        known.Identifier.ShouldBe("mmi1x2");
        known.PdkSource.ShouldBe("testpdk");
        known.WidthUm.ShouldBe(30, Tolerance);
        known.HeightUm.ShouldBe(10, Tolerance);
        known.Pins.Count.ShouldBe(2);
        known.Pins[0].Name.ShouldBe("o1");
        known.Pins[0].XUm.ShouldBe(0, Tolerance);
        known.Pins[0].YUm.ShouldBe(5, Tolerance);
        known.Pins[0].AngleDegrees.ShouldBe(180, Tolerance);
        known.Pins[1].Name.ShouldBe("o2");
        known.Pins[1].XUm.ShouldBe(30, Tolerance);
    }

    [Fact]
    public void Resolver_UnknownCellName_ReturnsNull()
    {
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { MmiTemplate() });

        resolver("does_not_exist").ShouldBeNull();
    }

    [Fact]
    public void Resolver_DuplicateTemplateNamesAcrossPdks_FirstWinsAndIsNotedOnce()
    {
        // Same component name from two PDKs: first in library order wins, and
        // the collision is surfaced (once) instead of hiding silently.
        var second = MmiTemplate();
        second.PdkSource = "otherpdk";
        var notes = new List<string>();
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(
            new[] { MmiTemplate(), second }, notes);

        var known = resolver("mmi1x2");
        known.ShouldNotBeNull();

        known.PdkSource.ShouldBe("testpdk", "first in enumeration order wins");
        resolver("mmi1x2"); // resolving again must not repeat the note
        var note = notes.ShouldHaveSingleItem();
        note.ShouldContain("mmi1x2");
        note.ShouldContain("testpdk");
        note.ShouldContain("otherpdk");
    }

    [Fact]
    public async Task Resolver_HashSuffixedCellName_ResolvesToBaseTemplate()
    {
        // The importer retries hash-stripped candidates with this resolver:
        // "mmi1x2_A1B2C3" → "mmi1x2" hits the template.
        var templates = new[] { MmiTemplate() };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(templates);

        var library = await new GdsReader().ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("mmi1x2_A1B2C3", 0, 0)
            .EndCell()
            .BeginCell("mmi1x2_A1B2C3")
                .Boundary(1, 0, (0, 0), (30000, 0), (30000, 10000), (0, 10000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray()));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { ResolveKnownComponent = resolver });

        result.ImportedCellDrafts.ShouldBeEmpty("the cell resolved to a known component — no draft");
        var instance = result.Instances.ShouldHaveSingleItem();
        instance.KnownComponentIdentifier.ShouldBe("mmi1x2");
        instance.PdkSource.ShouldBe("testpdk");
        instance.CellDraftName.ShouldBeNull();
    }

    [Fact]
    public async Task Resolver_UnknownCell_BecomesDraft()
    {
        var templates = new[] { MmiTemplate() };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(templates);

        var library = await new GdsReader().ReadAsync(new MemoryStream(GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("other_cell", 0, 0)
            .EndCell()
            .BeginCell("other_cell")
                .Boundary(1, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray()));

        var result = await GdsHierarchyImporter.ImportAsync(
            library, "TOP", new GdsHierarchyImportOptions { ResolveKnownComponent = resolver });

        result.ImportedCellDrafts.ShouldHaveSingleItem().CellName.ShouldBe("other_cell");
        result.Instances.ShouldHaveSingleItem().CellDraftName.ShouldBe("other_cell");
    }

    // ── Function-name keys and PDK precedence (issue #811) ───────────────────

    /// <summary>The bundled demofab template whose cells land in the GDS under the bare function name.</summary>
    private static ComponentTemplate BundledMmi2x2() => new()
    {
        Name = "2x2 MMI Coupler",
        PdkSource = "Demo PDK",
        NazcaFunctionName = "demo.mmi2x2_dp",
        IsCustom = false,
        WidthMicrometers = 120,
        HeightMicrometers = 60,
        PinDefinitions = new[]
        {
            new PinDefinition("in1", 0, 20, 180),
            new PinDefinition("out1", 120, 20, 0),
        },
    };

    [Fact]
    public void Resolver_FunctionNameLastSegment_PrefersBundledOverPriorGdsImport()
    {
        // The re-import scenario: yesterday's import of the same file registered
        // a black-box component NAMED after the cell; the bundled demofab
        // template only matches through its function name's last segment
        // (demo.mmi2x2_dp → mmi2x2_dp). The bundled PDK must win.
        var priorImport = new ComponentTemplate
        {
            Name = "mmi2x2_dp",
            PdkSource = "GDS Import - mzi",
            IsCustom = true,
            WidthMicrometers = 121,
            HeightMicrometers = 61,
            PinDefinitions = new[] { new PinDefinition("in1", 0, 20, 180) },
        };
        var notes = new List<string>();
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(
            new ComponentTemplate[] { priorImport, BundledMmi2x2() }, notes);

        var known = resolver("mmi2x2_dp");

        known.ShouldNotBeNull();
        known.Identifier.ShouldBe("2x2 MMI Coupler");
        known.PdkSource.ShouldBe("Demo PDK", "the bundled PDK outranks the stale same-named import");
        var note = notes.ShouldHaveSingleItem("the cross-PDK collision stays visible as a note");
        note.ShouldContain("mmi2x2_dp");
        note.ShouldContain("Demo PDK");
        note.ShouldContain("GDS Import - mzi");
    }

    [Fact]
    public void Resolver_SameNameInUserAndImportPdks_UserPdkWins()
    {
        var userTemplate = new ComponentTemplate
        {
            Name = "my_cell",
            PdkSource = "My PDK",
            IsCustom = true,
            WidthMicrometers = 10,
            HeightMicrometers = 5,
            PinDefinitions = new[] { new PinDefinition("o1", 0, 2, 180) },
        };
        var importTemplate = new ComponentTemplate
        {
            Name = "my_cell",
            PdkSource = "GDS Import - old",
            IsCustom = true,
            WidthMicrometers = 10,
            HeightMicrometers = 5,
            PinDefinitions = new[] { new PinDefinition("o1", 0, 2, 180) },
        };
        // Deliberately import-first enumeration order: the tier, not the order, decides.
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(
            new[] { importTemplate, userTemplate });

        var known = resolver("my_cell");

        known.ShouldNotBeNull();
        known.PdkSource.ShouldBe("My PDK", "a user PDK outranks a prior 'GDS Import - *' PDK");
    }

    [Fact]
    public void Resolver_PhaseShifter_HitByBothExactAndSanitizedCellName()
    {
        // The wrapper export names the cell Phase_Shifter (spaces cannot be GDS
        // cell-name characters); both name shapes must resolve to the template —
        // including a PARAMETERIZED one (the pin-label wrapper proves the
        // content, so the parameterless restriction only covers function-name
        // keys).
        var phaseShifter = new ComponentTemplate
        {
            Name = "Phase Shifter",
            PdkSource = "Demo PDK",
            NazcaFunctionName = "demo.eopm_dc",
            NazcaParameters = "length=500",
            IsCustom = false,
            WidthMicrometers = 500,
            HeightMicrometers = 60,
            PinDefinitions = new[]
            {
                new PinDefinition("in", 0, 30, 180),
                new PinDefinition("out", 500, 30, 0),
            },
        };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { phaseShifter });

        resolver("Phase Shifter").ShouldNotBeNull().Identifier.ShouldBe("Phase Shifter");
        resolver("Phase_Shifter").ShouldNotBeNull().Identifier.ShouldBe("Phase Shifter");
        resolver("eopm_dc").ShouldBeNull(
            "a parameterized template registers no function-name keys — the cell name " +
            "cannot prove which length the geometry carries");
    }

    [Fact]
    public void Resolver_SynthesizedNazcaPrefixedCellName_ResolvesToFunctionlessTemplate()
    {
        // A function-less template (e.g. a prior import) exports under the
        // synthesized fallback function name nazca_<name> — the cell name must
        // resolve back to the template.
        var imported = new ComponentTemplate
        {
            Name = "mmi1x2_sh",
            PdkSource = "GDS Import - old",
            IsCustom = true,
            WidthMicrometers = 80,
            HeightMicrometers = 55,
            PinDefinitions = new[] { new PinDefinition("in", 0, 27, 180) },
        };
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(new[] { imported });

        var known = resolver("nazca_mmi1x2_sh");

        known.ShouldNotBeNull();
        known.Identifier.ShouldBe("mmi1x2_sh");
        known.PdkSource.ShouldBe("GDS Import - old");
    }

    [Fact]
    public void Resolver_AmbiguousFunctionSegment_NeverGuessed()
    {
        // The real bundled library: '2x2 MMI Coupler' and 'Directional Coupler'
        // share the demofab function demo.mmi2x2_dp — the last-segment key is
        // ambiguous within the bundled tier, so the cell resolves to NOTHING
        // (a new draft), never a guess.
        var directionalCoupler = new ComponentTemplate
        {
            Name = "Directional Coupler",
            PdkSource = "Demo PDK",
            NazcaFunctionName = "demo.mmi2x2_dp",
            IsCustom = false,
            WidthMicrometers = 120,
            HeightMicrometers = 60,
            PinDefinitions = new[] { new PinDefinition("in1", 0, 20, 180) },
        };
        var notes = new List<string>();
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(
            new ComponentTemplate[] { BundledMmi2x2(), directionalCoupler }, notes);

        resolver("mmi2x2_dp").ShouldBeNull(
            "two bundled templates share the function name — ambiguous, never guessed");
        notes.ShouldBeEmpty();
    }
}

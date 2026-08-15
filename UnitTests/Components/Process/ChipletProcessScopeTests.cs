using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Per-chiplet process scoping (issue #935): a component group can act as a chiplet with
/// its own fabrication process — pinned explicitly via <see cref="ComponentGroup.ProcessBinding"/>
/// or derived live from its children (<see cref="GroupProcessPolicy.DeriveProcessBinding"/>).
/// Placement and paste checks resolve against the target chiplet's process; ungrouped
/// content stays checked against the canvas-level active process.
/// </summary>
public class ChipletProcessScopeTests
{
    private static readonly ProcessGroup SoiGroup = new(
        "SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo" });
    private static readonly ProcessGroup InPGroup = new(
        "InP", new ProcessFingerprint("InP", 300, "SiO2", 1550, "InP"), new[] { "HHI-InP" });
    private static readonly IReadOnlyList<ProcessGroup> Catalog = new[] { SoiGroup, InPGroup };

    private static ActiveProcessSelection Soi() => ActiveProcessSelection.ForGroup(SoiGroup);
    private static ActiveProcessSelection InP() => ActiveProcessSelection.ForGroup(InPGroup);

    /// <summary>Maps a child's NazcaFunctionName to a PDK source, mimicking the library lookup.</summary>
    private static string? Resolve(Component component) => component.NazcaFunctionName switch
    {
        "member_func" => "Demo",
        "foreign_func" => "HHI-InP",
        _ => null
    };

    private static PlacementPolicyContext Context(ActiveProcessSelection? active) =>
        new(() => active,
            () => Array.Empty<string>(),
            component => Resolve(component),
            getProcessCatalog: () => Catalog);

    private static ComponentGroup BuildGroup(string name, params string[] childNazcaFunctions)
    {
        var group = new ComponentGroup(name) { PhysicalX = 0, PhysicalY = 0 };
        for (int i = 0; i < childNazcaFunctions.Length; i++)
        {
            group.AddChild(new Component(
                new Dictionary<int, SMatrix>(),
                new List<Slider>(),
                childNazcaFunctions[i],
                "",
                new Part[1, 1] { { new Part() } },
                -1,
                $"comp_{i}_{Guid.NewGuid():N}",
                DiscreteRotation.R0,
                new List<PhysicalPin>())
            {
                PhysicalX = i * 100,
                PhysicalY = 0,
                WidthMicrometers = 50,
                HeightMicrometers = 30
            });
        }
        return group;
    }

    // ── DeriveProcessBinding ────────────────────────────────────────────────────

    [Fact]
    public void DeriveProcessBinding_UniformChildren_BindsToTheirProcess()
    {
        var binding = GroupProcessPolicy.DeriveProcessBinding(new[] { "HHI-InP", "HHI-InP" }, Catalog);

        binding.ShouldNotBeNull();
        binding!.DisplayName.ShouldBe("InP");
        binding.IsPlayground.ShouldBeFalse();
        binding.MemberPdkNames.ShouldBe(new[] { "HHI-InP" });
    }

    [Fact]
    public void DeriveProcessBinding_ChildrenSpanningTwoProcesses_ReturnsNull()
    {
        GroupProcessPolicy.DeriveProcessBinding(new[] { "Demo", "HHI-InP" }, Catalog)
            .ShouldBeNull("no single process can fabricate a mixed group — it is no chiplet");
    }

    [Fact]
    public void DeriveProcessBinding_OnlyExemptChildren_ReturnsNull()
    {
        GroupProcessPolicy.DeriveProcessBinding(new string?[] { null, "Built-in" }, Catalog)
            .ShouldBeNull("built-in/tool content carries no process");

        GroupProcessPolicy.DeriveProcessBinding(new[] { "Analysis Tools" }, Catalog, new[] { "Analysis Tools" })
            .ShouldBeNull("process-agnostic tool PDKs carry no process");
    }

    [Fact]
    public void DeriveProcessBinding_EmptyCatalog_ReturnsNull()
    {
        GroupProcessPolicy.DeriveProcessBinding(new[] { "HHI-InP" }, Array.Empty<ProcessGroup>())
            .ShouldBeNull("an unwired catalog must not fabricate bindings (legacy behavior)");
    }

    [Fact]
    public void DeriveProcessBinding_MatchesCaseInsensitively()
    {
        GroupProcessPolicy.DeriveProcessBinding(new[] { "hhi-inp" }, Catalog)
            .ShouldNotBeNull();
    }

    // ── ResolveChipletProcess ───────────────────────────────────────────────────

    [Fact]
    public void ResolveChipletProcess_ExplicitBindingWinsOverChildren()
    {
        var chiplet = BuildGroup("Chiplet", "foreign_func");
        chiplet.ProcessBinding = Soi();

        Context(active: null).ResolveChipletProcess(chiplet)!.DisplayName.ShouldBe("SOI 220");
    }

    [Fact]
    public void ResolveChipletProcess_UnboundGroup_DerivesFromChildren()
    {
        var chiplet = BuildGroup("Chiplet", "foreign_func", "foreign_func");

        Context(active: null).ResolveChipletProcess(chiplet)!.DisplayName.ShouldBe("InP");
    }

    [Fact]
    public void ResolveChipletProcess_MixedOrProcesslessGroup_IsUnbound()
    {
        Context(active: null).ResolveChipletProcess(BuildGroup("Mixed", "member_func", "foreign_func"))
            .ShouldBeNull();
        Context(active: null).ResolveChipletProcess(BuildGroup("Tools", "unknown_func"))
            .ShouldBeNull();
    }

    // ── CheckPlacementAt ────────────────────────────────────────────────────────

    [Fact]
    public void CheckPlacementAt_OntoBoundChiplet_ResolvesAgainstChipletProcess()
    {
        var context = Context(Soi());
        var chiplet = BuildGroup("InP Chiplet", "foreign_func");

        context.CheckPlacementAt("HHI-InP", chiplet).IsAllowed
            .ShouldBeTrue("the chiplet's own process scopes the drop, not the canvas lock");

        var (allowed, reason) = context.CheckPlacementAt("Demo", chiplet);
        allowed.ShouldBeFalse("the canvas process is foreign TO THE CHIPLET");
        reason.ShouldNotBeNull();
        reason!.ShouldContain("InP Chiplet");
        reason.ShouldContain("chiplet");
    }

    [Fact]
    public void CheckPlacementAt_OngroundedCanvas_KeepsCanvasLock()
    {
        var context = Context(Soi());

        context.CheckPlacementAt("HHI-InP", targetGroup: null).IsAllowed
            .ShouldBeFalse("ungrouped foreign content stays rejected by the canvas lock");
        context.CheckPlacementAt("Demo", targetGroup: null).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void CheckPlacementAt_OntoUnboundGroup_UsesCanvasLock()
    {
        var context = Context(Soi());
        var toolGroup = BuildGroup("Tool Group", "unknown_func");

        context.CheckPlacementAt("HHI-InP", toolGroup).IsAllowed
            .ShouldBeFalse("an unbound group has no process scope of its own");
    }

    // ── CheckGroupPlacementAt ───────────────────────────────────────────────────

    [Fact]
    public void CheckGroupPlacementAt_UniformForeignGroup_OnLockedCanvas_PlacesAsChiplet()
    {
        var context = Context(Soi());
        var chiplet = BuildGroup("InP Chiplet", "foreign_func", "foreign_func");

        var (allowed, reason, derivedBinding) = context.CheckGroupPlacementAt(chiplet, targetGroup: null, "InP Chiplet");

        allowed.ShouldBeTrue("a uniformly foreign group is placeable as its own chiplet");
        reason.ShouldBeNull();
        derivedBinding.ShouldNotBeNull("the caller must pin the derived binding onto the placed instance");
        derivedBinding!.DisplayName.ShouldBe("InP");
    }

    [Fact]
    public void CheckGroupPlacementAt_MixedGroup_OnLockedCanvas_StaysBlocked()
    {
        var context = Context(Soi());
        var mixed = BuildGroup("Mixed", "member_func", "foreign_func");

        var (allowed, reason, derivedBinding) = context.CheckGroupPlacementAt(mixed, targetGroup: null, "Mixed");

        allowed.ShouldBeFalse("a group no single process can fabricate is no chiplet");
        derivedBinding.ShouldBeNull();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("HHI-InP");
        reason.ShouldContain("monolithic");
    }

    [Fact]
    public void CheckGroupPlacementAt_MemberGroup_OnLockedCanvas_PlacesAndBindsToCanvasProcess()
    {
        var context = Context(Soi());
        var group = BuildGroup("SOI Pair", "member_func", "member_func");

        var (allowed, _, derivedBinding) = context.CheckGroupPlacementAt(group, targetGroup: null, "SOI Pair");

        allowed.ShouldBeTrue();
        derivedBinding.ShouldNotBeNull("a member group is pinned to the process it belongs to");
        derivedBinding!.DisplayName.ShouldBe("SOI 220");
    }

    [Fact]
    public void CheckGroupPlacementAt_OntoBoundChiplet_ResolvesAgainstChipletProcess()
    {
        var context = Context(Soi());
        var chiplet = BuildGroup("InP Chiplet", "foreign_func");

        var matching = BuildGroup("InP Add-on", "foreign_func");
        context.CheckGroupPlacementAt(matching, chiplet, "InP Add-on").IsAllowed
            .ShouldBeTrue("same-process content may join the chiplet");

        var (allowed, reason, _) = context.CheckGroupPlacementAt(BuildGroup("SOI Add-on", "member_func"), chiplet, "SOI Add-on");
        allowed.ShouldBeFalse("canvas-member content is foreign to the chiplet");
        reason.ShouldNotBeNull();
        reason!.ShouldContain("InP Chiplet");
    }

    // ── IsPasteEntryAllowed ─────────────────────────────────────────────────────

    [Fact]
    public void IsPasteEntryAllowed_LooseForeignComponent_StaysBlocked()
    {
        Context(Soi()).IsPasteEntryAllowed(isGroupEntry: false, new[] { "HHI-InP" })
            .ShouldBeFalse("a loose foreign component must not slip through as a pseudo-chiplet");
    }

    [Fact]
    public void IsPasteEntryAllowed_CopiedUniformForeignGroup_PastesAsChiplet()
    {
        Context(Soi()).IsPasteEntryAllowed(isGroupEntry: true, new[] { "HHI-InP", "HHI-InP" })
            .ShouldBeTrue("a copied chiplet keeps its own process scope across paste");
    }

    [Fact]
    public void IsPasteEntryAllowed_CopiedMixedGroup_StaysBlocked()
    {
        Context(Soi()).IsPasteEntryAllowed(isGroupEntry: true, new[] { "Demo", "HHI-InP" })
            .ShouldBeFalse("a mixed group is no chiplet and contains a foreign child");
    }

    [Fact]
    public void IsPasteEntryAllowed_MemberContent_AlwaysAllowed()
    {
        Context(Soi()).IsPasteEntryAllowed(isGroupEntry: false, new[] { "Demo" }).ShouldBeTrue();
        Context(Soi()).IsPasteEntryAllowed(isGroupEntry: true, new[] { "Demo", null }).ShouldBeTrue();
    }

    // ── DeepCopy carries the binding ────────────────────────────────────────────

    [Fact]
    public void DeepCopy_CarriesProcessBinding()
    {
        var chiplet = BuildGroup("InP Chiplet", "foreign_func");
        chiplet.ProcessBinding = InP();

        var copy = chiplet.DeepCopy();

        copy.ProcessBinding.ShouldNotBeNull();
        copy.ProcessBinding!.DisplayName.ShouldBe("InP");
    }
}

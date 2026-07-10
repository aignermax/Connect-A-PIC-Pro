using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Tests for <see cref="GroupTemplateOverrides"/> (issue #720): collecting per-member
/// override JSON when saving a group template and correlating template-child identifiers
/// with the identifiers of a deep-copied instance.
/// </summary>
public class GroupTemplateOverridesTests
{
    [Fact]
    public void Collect_ReturnsOnlyChildrenWithOverrides()
    {
        var group = CreateGroup("g", 3);
        var overriddenId = group.ChildComponents[1].Identifier;

        var collected = GroupTemplateOverrides.Collect(
            group,
            id => id == overriddenId ? "{\"RawCode\":\"x\"}" : null);

        collected.Count.ShouldBe(1);
        collected[overriddenId].ShouldBe("{\"RawCode\":\"x\"}");
    }

    [Fact]
    public void Collect_IncludesNestedGroupChildren()
    {
        var outer = CreateGroup("outer", 1);
        var nested = CreateGroup("nested", 2);
        outer.AddChild(nested);
        var nestedChildId = nested.ChildComponents[0].Identifier;

        var collected = GroupTemplateOverrides.Collect(
            outer,
            id => id == nestedChildId ? "{}" : null);

        collected.Count.ShouldBe(1);
        collected.ShouldContainKey(nestedChildId);
    }

    [Fact]
    public void BuildIdentifierMap_MapsTemplateChildrenToDeepCopyChildren()
    {
        var original = CreateGroup("g", 2);
        var copy = original.DeepCopy();

        var map = GroupTemplateOverrides.BuildIdentifierMap(original, copy);

        map.Count.ShouldBe(2);
        for (var i = 0; i < 2; i++)
        {
            map[original.ChildComponents[i].Identifier]
                .ShouldBe(copy.ChildComponents[i].Identifier);
        }
    }

    [Fact]
    public void BuildIdentifierMap_MapsNestedGroupChildren()
    {
        var outer = CreateGroup("outer", 1);
        var nested = CreateGroup("nested", 2);
        outer.AddChild(nested);
        var copy = outer.DeepCopy();

        var map = GroupTemplateOverrides.BuildIdentifierMap(outer, copy);

        // 1 direct child + 2 nested children; the nested GROUP itself is not mapped
        map.Count.ShouldBe(3);
        var copiedNested = (ComponentGroup)copy.ChildComponents[1];
        map[nested.ChildComponents[0].Identifier]
            .ShouldBe(copiedNested.ChildComponents[0].Identifier);
    }

    /// <summary>Creates a test group with the given number of single-pin children.</summary>
    private static ComponentGroup CreateGroup(string name, int childCount)
    {
        var group = new ComponentGroup(name) { PhysicalX = 0, PhysicalY = 0 };

        for (var i = 0; i < childCount; i++)
        {
            var child = new Component(
                new Dictionary<int, SMatrix>(),
                new List<Slider>(),
                "test_component",
                "",
                new Part[1, 1] { { new Part() } },
                -1,
                $"comp_{name}_{i}_{Guid.NewGuid():N}",
                DiscreteRotation.R0,
                new List<PhysicalPin>
                {
                    new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 0, AngleDegrees = 180 }
                })
            {
                PhysicalX = i * 100,
                PhysicalY = 0,
                WidthMicrometers = 50,
                HeightMicrometers = 30
            };
            group.AddChild(child);
        }

        return group;
    }
}

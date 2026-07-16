using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Persistence.PIR;

namespace UnitTests.UI;

/// <summary>
/// Builds the issue-#720 walkthrough scenario: a two-member group whose first member
/// carries a raw-code Nazca override in a simulated source-design override store.
/// Mirrors the scenario used by <c>PlaceGroupTemplateOverrideSeedingTests</c> so the
/// walkthrough renders exactly what the regression tests verify.
/// </summary>
internal static class Issue720WalkthroughScenario
{
    /// <summary>Creates the group, the overridden member's identifier, and the source store.</summary>
    public static (ComponentGroup group, string overriddenId,
        Dictionary<string, NazcaCodeOverride> sourceStore) CreateGroupWithRawCodeOverride(
        string rawCodeMarker)
    {
        var group = new ComponentGroup("RawCodeGroup") { PhysicalX = 0, PhysicalY = 0 };
        for (var i = 0; i < 2; i++)
            group.AddChild(CreateChild(i));

        var overriddenId = group.ChildComponents[0].Identifier;
        var sourceStore = new Dictionary<string, NazcaCodeOverride>
        {
            [overriddenId] = new NazcaCodeOverride
            {
                RawCode = $"with nd.Cell('custom') as component:\n    {rawCodeMarker}\n",
                Backend = OverrideBackend.Nazca,
                OverrideWidthMicrometers = 123.5,
                OverrideHeightMicrometers = 0.45,
            }
        };
        return (group, overriddenId, sourceStore);
    }

    private static Component CreateChild(int index)
    {
        return new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            $"comp_{index}_{Guid.NewGuid():N}",
            DiscreteRotation.R0,
            new List<PhysicalPin>
            {
                new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 0, AngleDegrees = 180 }
            })
        {
            PhysicalX = index * 100,
            PhysicalY = 0,
            WidthMicrometers = 50,
            HeightMicrometers = 30,
        };
    }
}

using CAP.Avalonia.Services.ComponentRegistry;
using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryDownload;

/// <summary>
/// Tests for <see cref="RegistryArtifactSelector"/> (issue #773): only real,
/// non-withdrawn data qualifies for adoption; clean artifacts beat disputed
/// ones; simulated beats measured at equal trust.
/// </summary>
public class RegistryArtifactSelectorTests
{
    private static ArtifactRef Artifact(string file, string status) => new()
    {
        File = file,
        Status = status,
        Provenance = new ArtifactProvenance { Method = "fdtd", CreatedBy = "tester", Date = "2026-01-01" },
    };

    private static ComponentManifest Manifest(string[] simulated, string[] measured) => new()
    {
        Id = "widget",
        Name = "Widget",
        Process = "generic-si220",
        Artifacts = new ComponentArtifacts
        {
            Simulated = simulated.Select((s, i) => Artifact($"simulated/s{i}.json", s)).ToList(),
            Measured = measured.Select((s, i) => Artifact($"measured/m{i}.json", s)).ToList(),
        },
    };

    [Fact]
    public void Select_PrefersSimulated_OverMeasured()
    {
        var choice = RegistryArtifactSelector.Select(Manifest(["demo"], ["verified"]));

        choice.ShouldNotBeNull();
        choice.Tier.ShouldBe("simulated");
        choice.Artifact.File.ShouldBe("simulated/s0.json");
        choice.IsDisputed.ShouldBeFalse();
    }

    [Fact]
    public void Select_FallsBackToMeasured_WhenNoSimulated()
    {
        var choice = RegistryArtifactSelector.Select(Manifest([], ["verified"]));

        choice.ShouldNotBeNull();
        choice.Tier.ShouldBe("measured");
        choice.IsDisputed.ShouldBeFalse();
    }

    [Fact]
    public void Select_SkipsWithdrawn_AndTakesNextUsable()
    {
        var choice = RegistryArtifactSelector.Select(Manifest(["withdrawn", "unverified"], []));

        choice.ShouldNotBeNull();
        choice.Artifact.File.ShouldBe("simulated/s1.json");
    }

    [Fact]
    public void Select_WithdrawnOnly_ReturnsNull()
    {
        RegistryArtifactSelector.Select(Manifest(["withdrawn"], ["withdrawn"])).ShouldBeNull();
    }

    [Fact]
    public void Select_NoArtifactsAtAll_ReturnsNull()
    {
        RegistryArtifactSelector.Select(Manifest([], [])).ShouldBeNull();
    }

    [Fact]
    public void Select_DisputedSimulated_WithCleanMeasured_PrefersCleanMeasured()
    {
        var choice = RegistryArtifactSelector.Select(Manifest(["disputed"], ["verified"]));

        choice.ShouldNotBeNull();
        choice.Tier.ShouldBe("measured");
        choice.IsDisputed.ShouldBeFalse();
    }

    [Fact]
    public void Select_DisputedOnlyArtifact_IsChosenAndFlagged()
    {
        var choice = RegistryArtifactSelector.Select(Manifest(["disputed"], []));

        choice.ShouldNotBeNull();
        choice.Tier.ShouldBe("simulated");
        choice.IsDisputed.ShouldBeTrue();
    }

    [Fact]
    public void Select_DisputedSimulated_WithDisputedMeasured_PrefersSimulated()
    {
        var choice = RegistryArtifactSelector.Select(Manifest(["disputed"], ["disputed"]));

        choice.ShouldNotBeNull();
        choice.Tier.ShouldBe("simulated");
        choice.IsDisputed.ShouldBeTrue();
    }
}

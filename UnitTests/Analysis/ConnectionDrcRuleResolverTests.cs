using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// The per-connection DRC rule resolver (issue #936) keys one connection's width and
/// spacing limits to the PDK processes of its OWN endpoint components instead of the
/// design-wide rule set of the active process' first member PDK. Both endpoint
/// processes contribute — the stricter side governs — and PDKs that declare no
/// minimum stay silent (the #926 no-invented-values rule). Exercised on the real
/// bundled PDKs (Cornerstone SiN declares minWidthUm 0.25 + spacing 0.25; SiEPIC
/// declares neither).
/// </summary>
public class ConnectionDrcRuleResolverTests
{
    private const string CornerstonePdkFile = "cornerstone-sin-pdk.json";
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";
    private const double CornerstoneDeclaredSpacingUm = 0.25;

    [Fact]
    public void TwoProcesses_BothContribute_StricterSpacingGoverns()
    {
        var (cornerstone, siepic) = LoadBundled();

        var rules = ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            cornerstone.Name, siepic.Name, new[] { cornerstone, siepic });

        rules.ShouldNotBeNull();
        rules!.WidthRules.Count.ShouldBe(2,
            "only Cornerstone declares minWidthUm (xs_nc + xs_no) — SiEPIC contributes nothing");
        rules.WidthRules.ShouldAllBe(r => r.MinWidthMicrometers == 0.25);
        rules.MinSpacingMicrometers.ShouldBe(CornerstoneDeclaredSpacingUm,
            "Cornerstone declares 0.25, SiEPIC declares nothing — the stricter side governs");
    }

    [Fact]
    public void SamePdkOnBothEnds_RulesAreNotDuplicated()
    {
        var (cornerstone, _) = LoadBundled();

        var rules = ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            cornerstone.Name, cornerstone.Name, new[] { cornerstone });

        rules.ShouldNotBeNull();
        rules!.WidthRules.Count.ShouldBe(2,
            "one process contributes its two cross-section rules exactly once");
        rules.MinSpacingMicrometers.ShouldBe(CornerstoneDeclaredSpacingUm);
    }

    [Fact]
    public void UndeclaringPdk_StaysSilent_ButIsNotNoOpinion()
    {
        var (_, siepic) = LoadBundled();

        var rules = ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            siepic.Name, siepic.Name, new[] { siepic });

        rules.ShouldNotBeNull(
            "the PDK resolves — it simply declares nothing (distinct from 'no PDK opinion')");
        rules!.WidthRules.ShouldBeEmpty("SiEPIC declares no minWidthUm — no invented values (#926)");
        rules.MinSpacingMicrometers.ShouldBe(0, "SiEPIC declares no minWaveguideSpacingUm");
    }

    [Fact]
    public void NoResolvableEndpoint_ReturnsNull()
    {
        var (cornerstone, siepic) = LoadBundled();
        var drafts = new PdkDraft[] { cornerstone, siepic };

        ConnectionDrcRuleResolver.ResolveForEndpointPdkNames("deleted-pdk", null, drafts)
            .ShouldBeNull("unknown PDK names carry no process opinion");
        ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(null, null, drafts)
            .ShouldBeNull();
        ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(cornerstone.Name, siepic.Name, null)
            .ShouldBeNull("without loaded drafts nothing can resolve");
    }

    [Fact]
    public void OneEndpointUnresolvable_OtherSideStillGoverns()
    {
        var (cornerstone, _) = LoadBundled();

        var rules = ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            cornerstone.Name, null, new[] { cornerstone });

        rules.ShouldNotBeNull();
        rules!.WidthRules.Count.ShouldBe(2);
        rules.MinSpacingMicrometers.ShouldBe(CornerstoneDeclaredSpacingUm);
    }

    [Fact]
    public void Spacing_StricterDeclaredValueWins()
    {
        var loose = SyntheticDraft("Loose", spacingUm: 0.3);
        var strict = SyntheticDraft("Strict", spacingUm: 0.7);

        var rules = ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            loose.Name, strict.Name, new[] { loose, strict });

        rules.ShouldNotBeNull();
        rules!.MinSpacingMicrometers.ShouldBe(0.7,
            "a cross-chiplet connection respects the stricter of the two endpoint processes");
    }

    private static PdkDraft SyntheticDraft(string name, double spacingUm) =>
        new()
        {
            Name = name,
            Process = new ProcessDefinition { Name = name, MinWaveguideSpacingUm = spacingUm },
        };

    private static (PdkDraft Cornerstone, PdkDraft Siepic) LoadBundled() =>
        (LoadPdk(CornerstonePdkFile), LoadPdk(SiepicPdkFile));

    private static PdkDraft LoadPdk(string fileName) =>
        new PdkLoader().LoadFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName));
}

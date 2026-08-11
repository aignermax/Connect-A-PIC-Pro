using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Import.Gds.LayerCensus;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Headless tests for the census/suggestion half of
/// <see cref="GdsImportDialogViewModel"/>: analysis fills the census rows and
/// suggestion chips, census clicks append to the last-focused field, accepted
/// suggestions land in their role's field — and nothing is ever prefilled.
/// </summary>
public class GdsImportDialogCensusTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdscensus-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>
    /// TOP with one waveguide instance, a route-like path on the unknown layer
    /// (37,0), and a child cell carrying port-style texts on the foundry layer
    /// (56,0) — one fixture exercising census, port suggestion and route
    /// suggestion at once.
    /// </summary>
    private static byte[] FoundryStyleLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wg", 0, 0)
            .Path(37, 0, 500, 0, (0, 0), (50000, 0))
        .EndCell()
        .BeginCell("wg")
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Text(56, 0, "opt_in", 0, 2000)
            .Text(56, 0, "opt_out", 10000, 2000)
        .EndCell()
        .EndLibrary()
        .ToArray();

    private GdsImportDialogViewModel CreateDialog(byte[] gdsBytes)
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "census.gds");
        File.WriteAllBytes(gdsPath, gdsBytes);
        var service = _host.CreateService();
        var executor = new GdsPlacementExecutor(
            new DesignCanvasViewModel(), new CommandManager(), () => new List<ComponentTemplate>());
        return new GdsImportDialogViewModel(gdsPath, service, executor);
    }

    private async Task<GdsImportDialogViewModel> AnalyzedDialog()
    {
        var vm = CreateDialog(FoundryStyleLibrary());
        await vm.StartAnalysisAsync();
        vm.HasError.ShouldBeFalse(vm.ErrorText);
        return vm;
    }

    [Fact]
    public async Task Analysis_PopulatesCensusRows_WithCounts()
    {
        var vm = await AnalyzedDialog();

        vm.HasCensus.ShouldBeTrue();
        var textRow = vm.CensusRows.Single(r => r.Entry is { Layer: 56, Datatype: 0 });
        textRow.Entry.TextCount.ShouldBe(2);
        textRow.PairText.ShouldBe("(56,0)");
        textRow.TextCellsText.ShouldContain("wg");
        vm.CensusRows.Single(r => r.Entry is { Layer: 37, Datatype: 0 }).Entry.PathCount.ShouldBe(1);
    }

    [Fact]
    public async Task Analysis_AutoAppliesConfidentSuggestions_LeavesUnknownToTheUser()
    {
        var vm = await AnalyzedDialog();

        vm.HasSuggestions.ShouldBeTrue();
        vm.SuggestionChips.ShouldContain(c =>
            c.Suggestion.Layer == 56 && c.Suggestion.Role == GdsLayerRole.PortLabels);
        vm.SuggestionChips.ShouldContain(c =>
            c.Suggestion.Layer == 37 && c.Suggestion.Role == GdsLayerRole.RoutingUnknown);
        // confident suggestions (foundry table / text evidence) are auto-applied:
        vm.PortLayersText.ShouldBe("1,10;501,1; 56,0");
        vm.SuggestionChips.Single(c => c.Suggestion.Layer == 56).IsAccepted.ShouldBeTrue();
        // "routing, kind unknown" is never auto-applied:
        vm.WaveguideLayersText.ShouldBe("1,0; 1111,0");
        vm.MetalLayersText.ShouldBe("11,0; 12,0; 13,0");
    }

    [Fact]
    public async Task AppendCensusRow_AppendsToTheActiveField_Idempotently()
    {
        var vm = await AnalyzedDialog();
        var row = vm.CensusRows.Single(r => r.Entry is { Layer: 56, Datatype: 0 });
        vm.ActiveLayerField = GdsLayerFieldTarget.PortLabels;

        vm.AppendCensusRowCommand.Execute(row);
        vm.AppendCensusRowCommand.Execute(row);

        vm.PortLayersText.ShouldBe("1,10;501,1; 56,0");
        vm.WaveguideLayersText.ShouldBe("1,0; 1111,0");
    }

    [Fact]
    public async Task AppendCensusRow_HonorsTheLastFocusedField()
    {
        var vm = await AnalyzedDialog();
        var row = vm.CensusRows.Single(r => r.Entry is { Layer: 37, Datatype: 0 });
        vm.ActiveLayerField = GdsLayerFieldTarget.Metal;

        vm.AppendCensusRowCommand.Execute(row);

        vm.MetalLayersText.ShouldBe("11,0; 12,0; 13,0; 37,0");
        vm.PortLayersText.ShouldNotContain("37,0"); // the click went to Metal, not Ports
    }

    [Fact]
    public async Task AcceptSuggestion_TogglesTheAutoAppliedPairInAndOut()
    {
        var vm = await AnalyzedDialog();
        var chip = vm.SuggestionChips.Single(c =>
            c.Suggestion.Layer == 56 && c.Suggestion.Role == GdsLayerRole.PortLabels);
        chip.IsAccepted.ShouldBeTrue("confident suggestions are auto-applied on analysis");

        vm.AcceptSuggestionCommand.Execute(chip);
        vm.PortLayersText.ShouldBe("1,10; 501,1"); // remove normalizes the separator
        chip.IsAccepted.ShouldBeFalse("first click undoes the auto-accept");

        vm.AcceptSuggestionCommand.Execute(chip);
        vm.PortLayersText.ShouldBe("1,10; 501,1; 56,0");
        chip.IsAccepted.ShouldBeTrue();
    }

    [Fact]
    public async Task EditingThePairBackOut_UnmarksTheChip()
    {
        var vm = await AnalyzedDialog();
        var chip = vm.SuggestionChips.Single(c =>
            c.Suggestion.Layer == 56 && c.Suggestion.Role == GdsLayerRole.PortLabels);
        chip.IsAccepted.ShouldBeTrue("auto-applied on analysis");

        vm.PortLayersText = "1,10";

        chip.IsAccepted.ShouldBeFalse();
    }

    [Fact]
    public async Task RoutingUnknownChip_IsNotAcceptable_AcceptLeavesTheFieldsUntouched()
    {
        var vm = await AnalyzedDialog();
        var chip = vm.SuggestionChips.Single(c =>
            c.Suggestion.Layer == 37 && c.Suggestion.Role == GdsLayerRole.RoutingUnknown);

        chip.IsAcceptable.ShouldBeFalse("undecidable suggestions inform but are never auto-assignable");

        vm.AcceptSuggestionCommand.Execute(chip);

        vm.WaveguideLayersText.ShouldBe("1,0; 1111,0");
        chip.IsAccepted.ShouldBeFalse();
    }

    [Fact]
    public async Task AcceptAllSuggestions_ReAppliesManuallyRemovedPairs_SkipsUnknown()
    {
        var vm = await AnalyzedDialog();
        // auto-apply already accepted every confident chip — the button starts disabled:
        vm.AcceptAllSuggestionsCommand.CanExecute(null).ShouldBeFalse();

        vm.PortLayersText = "1,10"; // user strips the auto-applied port layer
        vm.AcceptAllSuggestionsCommand.CanExecute(null).ShouldBeTrue();

        vm.AcceptAllSuggestionsCommand.Execute(null);

        vm.PortLayersText.ShouldContain("56,0");
        // the routing-unknown suggestion is never bulk-acceptable:
        vm.WaveguideLayersText.ShouldNotContain("37,0");
    }

    [Fact]
    public async Task Reanalysis_RebuildsCensusAndSuggestions_WithoutDuplicates()
    {
        var vm = await AnalyzedDialog();
        var rowsAfterFirst = vm.CensusRows.Count;
        var chipsAfterFirst = vm.SuggestionChips.Count;

        await vm.StartAnalysisAsync();

        vm.CensusRows.Count.ShouldBe(rowsAfterFirst);
        vm.SuggestionChips.Count.ShouldBe(chipsAfterFirst);
        vm.PortLayersText.ShouldBe("1,10;501,1; 56,0",
            "auto-apply is idempotent across re-analysis");
    }
}

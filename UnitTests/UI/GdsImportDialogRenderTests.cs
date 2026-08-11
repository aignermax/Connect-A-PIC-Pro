using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Views.Dialogs;
using Shouldly;
using UnitTests.Import.Gds;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Render smoke test for the GDS import dialog's census/suggestion UI (#848):
/// with analysis results present, showing the dialog must build the chip and
/// census DataTemplates without throwing. Regression guard for the field crash
/// where the <c>$parent[Window].((vm:…)DataContext)</c> casts failed runtime
/// type resolution (<c>using:</c> xmlns form) the moment the suggestions
/// rendered — the ViewModel-level tests never exercised the view.
/// </summary>
[Collection("LocalizationSingleton")]
public class GdsImportDialogRenderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsdlg-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>Same shape as the census fixture: port-style texts on (56,0)
    /// plus a route-like path on the unknown (37,0), so both suggestion chips
    /// and census rows are non-empty.</summary>
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

    [AvaloniaFact]
    public async Task Dialog_WithSuggestionsAndCensus_RendersWithoutThrowing()
    {
        Directory.CreateDirectory(_root);
        var gdsPath = Path.Combine(_root, "census.gds");
        File.WriteAllBytes(gdsPath, FoundryStyleLibrary());
        var service = _host.CreateService();
        var executor = new GdsPlacementExecutor(
            new DesignCanvasViewModel(), new CommandManager(), () => new List<ComponentTemplate>());
        var vm = new GdsImportDialogViewModel(gdsPath, service, executor);
        await vm.StartAnalysisAsync();
        vm.HasError.ShouldBeFalse(vm.ErrorText);
        vm.HasSuggestions.ShouldBeTrue("the chips template only builds when suggestions exist");
        vm.HasCensus.ShouldBeTrue("the census template only builds when rows exist");

        var dialog = new GdsImportDialog { DataContext = vm };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        // The layer-assignment section is collapsed by default — expanding it
        // builds the deferred chip/census templates (the runtime-cast crash site).
        dialog.FindControl<Expander>("LayerAssignmentExpander")
            .ShouldNotBeNull().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}

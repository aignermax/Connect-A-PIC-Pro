using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsImportButtonViewModel"/> (the library panel's "Import
/// GDS" button): the command must never let a failure escape as an unhandled task
/// exception — missing view wiring and a throwing file-dialog service surface on
/// the status callback instead. Also covers the hand-off of the zoom-to-fit
/// callback to the dialog ViewModel (the dialog owns the import's completion point).
/// </summary>
public class GdsImportButtonViewModelTests
{
    /// <summary>File-dialog stub whose picks throw, like a broken dialog backend.</summary>
    private sealed class ThrowingFileDialogService : IFileDialogService
    {
        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters)
            => throw new InvalidOperationException("dialog backend exploded");

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters)
            => throw new InvalidOperationException("dialog backend exploded");
    }

    /// <summary>File-dialog stub returning a fixed pick (the dialog host itself is faked away).</summary>
    private sealed class StubFileDialogService(string path) : IFileDialogService
    {
        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters)
            => Task.FromResult<string?>(path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters)
            => Task.FromResult<string?>(path);
    }

    private static GdsImportButtonViewModel CreateButton()
    {
        var canvas = new DesignCanvasViewModel();
        // Throwaway service/executor: the failure paths tested here never reach
        // an actual import — only the dialog hand-off is exercised.
        var importService = new CAP.Avalonia.Services.GdsImport.GdsImportService();
        var placementExecutor = new CAP.Avalonia.Services.GdsImport.GdsPlacementExecutor(
            canvas, new CommandManager(), () => Array.Empty<ComponentTemplate>());
        return new GdsImportButtonViewModel(importService, placementExecutor);
    }

    [Fact]
    public async Task OpenGdsImportDialog_MissingViewWiring_ReportsUnavailable()
    {
        var vm = CreateButton();
        string? status = null;
        vm.UpdateStatus = s => status = s;

        await vm.OpenGdsImportDialogCommand.ExecuteAsync(null);

        status.ShouldBe(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
    }

    [Fact]
    public async Task OpenGdsImportDialog_FileDialogThrows_ReportsStatusInsteadOfThrowing()
    {
        var vm = CreateButton();
        vm.FileDialogService = new ThrowingFileDialogService();
        vm.ShowImportDialogAsync = _ => Task.CompletedTask;
        string? status = null;
        vm.UpdateStatus = s => status = s;

        // Awaiting the command task would rethrow an unhandled exception — the
        // try/catch in the command must keep the task clean.
        await vm.OpenGdsImportDialogCommand.ExecuteAsync(null);

        status.ShouldBe(string.Format(
            LocalizationService.Instance.Translate("GdsImport.StatusOpenFailed"),
            "dialog backend exploded"));
    }

    [Fact]
    public async Task OpenGdsImportDialog_FilePicked_HandsZoomToFitCallbackToTheDialog()
    {
        var vm = CreateButton();
        // The path is never read: analysis starts only when the (faked) dialog view opens.
        vm.FileDialogService = new StubFileDialogService("circuit.gds");
        GdsImportDialogViewModel? dialog = null;
        vm.ShowImportDialogAsync = d => { dialog = d; return Task.CompletedTask; };
        var fired = 0;
        vm.ZoomToFitAfterImport = (_, _) => fired++;

        await vm.OpenGdsImportDialogCommand.ExecuteAsync(null);

        var callback = dialog.ShouldNotBeNull("a picked file opens the import dialog")
            .ZoomToFitAfterImport.ShouldNotBeNull(
                "the button's zoom callback rides along so the dialog can fire it after placement");
        callback.Invoke(900, 800);
        fired.ShouldBe(1);
    }

    [Fact]
    public async Task OpenGdsImportDialog_NoZoomCallbackWired_DialogCallbackStaysNull()
    {
        var vm = CreateButton();
        vm.FileDialogService = new StubFileDialogService("circuit.gds");
        GdsImportDialogViewModel? dialog = null;
        vm.ShowImportDialogAsync = d => { dialog = d; return Task.CompletedTask; };

        await vm.OpenGdsImportDialogCommand.ExecuteAsync(null);

        dialog.ShouldNotBeNull().ZoomToFitAfterImport.ShouldBeNull(
            "without MainViewModel wiring (e.g. headless runs) there is no zoom to fire");
    }

    [Fact]
    public async Task OpenGdsImportDialogForFile_OpensDialogForThatFile()
    {
        // The File→Open dialog's GDS route: no file picking, the dialog opens
        // straight for the handed-over path (and analyzes it on open).
        var vm = CreateButton();
        GdsImportDialogViewModel? dialog = null;
        vm.ShowImportDialogAsync = d => { dialog = d; return Task.CompletedTask; };
        var fired = 0;
        vm.ZoomToFitAfterImport = (_, _) => fired++;

        await vm.OpenGdsImportDialogForFileAsync("picked.gds");

        var shown = dialog.ShouldNotBeNull("a routed file opens the import dialog directly");
        shown.GdsFilePath.ShouldBe("picked.gds");
        shown.ZoomToFitAfterImport.ShouldNotBeNull(
            "the zoom callback rides along, exactly as on the file-pick path")
            .Invoke(900, 800);
        fired.ShouldBe(1);
    }

    [Fact]
    public async Task OpenGdsImportDialogForFile_MissingViewWiring_ReportsUnavailable()
    {
        var vm = CreateButton();
        string? status = null;
        vm.UpdateStatus = s => status = s;

        await vm.OpenGdsImportDialogForFileAsync("picked.gds");

        status.ShouldBe(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
    }

    [Fact]
    public async Task OpenGdsImportDialogForFile_DialogHostThrows_ReportsStatusInsteadOfThrowing()
    {
        var vm = CreateButton();
        vm.ShowImportDialogAsync = _ => throw new InvalidOperationException("dialog host exploded");
        string? status = null;
        vm.UpdateStatus = s => status = s;

        await vm.OpenGdsImportDialogForFileAsync("picked.gds");

        status.ShouldBe(string.Format(
            LocalizationService.Instance.Translate("GdsImport.StatusOpenFailed"),
            "dialog host exploded"));
    }
}

using CAP.Avalonia.Services;
using Shouldly;
using UnitTests.Helpers;
using UnitTests;

namespace UnitTests.Export;

/// <summary>
/// Export-guard tests: triggering the Nazca export with GDS generation enabled but
/// no Nazca available must prompt instead of silently failing the GDS step.
/// </summary>
public class GdsExportGuardTests
{
    private sealed class RecordingMessageBox : IMessageBoxService
    {
        public int ChoiceToReturn { get; set; } = 2; // "Skip GDS"
        public int Calls { get; private set; }

        public Task<SavePromptResult> ShowSavePromptAsync(string message, string title) =>
            Task.FromResult(SavePromptResult.Cancel);

        public Task<int> ShowChoicePromptAsync(string message, string title, IReadOnlyList<string> buttonLabels)
        {
            Calls++;
            return Task.FromResult(ChoiceToReturn);
        }
    }

    private sealed class FixedPathFileDialog : IFileDialogService
    {
        private readonly string _path;
        public FixedPathFileDialog(string path) => _path = path;

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            Task.FromResult<string?>(_path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task ExportNazca_FileNameShadowsPythonModule_RefusesExport()
    {
        // Real-world failure: a script saved as "re.py" shadows Python's stdlib re module,
        // so numpy/nazca imports die with a circular-import error on ANY interpreter.
        var scriptPath = Path.Combine(Path.GetTempPath(), "re.py");
        var main = MainViewModelTestHelper.CreateMainViewModel();
        var fileOps = main.FileOperations;
        var messageBox = new RecordingMessageBox();
        fileOps.MessageBoxService = messageBox;
        fileOps.FileDialogService = new FixedPathFileDialog(scriptPath);
        string? lastStatus = null;
        fileOps.UpdateStatus = s => lastStatus = s;

        var component = TestComponentFactory.CreateStraightWaveGuide();
        main.Canvas.AddComponent(component, "TestTemplate");

        await fileOps.ExportNazcaCommand.ExecuteAsync(null);

        File.Exists(scriptPath).ShouldBeFalse();     // nichts geschrieben
        messageBox.Calls.ShouldBe(1);                // Nutzer wurde informiert
        lastStatus.ShouldNotBeNull();
        lastStatus!.ShouldContain("shadows");
    }

    [Fact]
    public async Task ExportNazca_GdsEnabledButNazcaMissing_PromptsAndSkipsGds()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"lunima-export-{Guid.NewGuid():N}.py");
        try
        {
            var main = MainViewModelTestHelper.CreateMainViewModel();
            var fileOps = main.FileOperations;
            var messageBox = new RecordingMessageBox { ChoiceToReturn = 2 };
            fileOps.MessageBoxService = messageBox;
            fileOps.FileDialogService = new FixedPathFileDialog(scriptPath);
            string? lastStatus = null;
            fileOps.UpdateStatus = s => lastStatus = s;

            // Deterministic "no Nazca": point the interpreter at a nonexistent path so the
            // guard's environment re-check cannot find a system Python with Nazca.
            var bogusPython = Path.Combine(Path.GetTempPath(), "no-python-here", "python");
            await fileOps.GdsExport.SetPythonPathAsync(bogusPython);
            fileOps.GdsExport.GenerateGdsEnabled = true;

            // Eine Komponente, damit der Export nicht am leeren Canvas abbricht:
            var component = TestComponentFactory.CreateStraightWaveGuide();
            main.Canvas.AddComponent(component, "TestTemplate");

            await fileOps.ExportNazcaCommand.ExecuteAsync(null);

            messageBox.Calls.ShouldBe(1);                    // Guard hat gefragt
            File.Exists(scriptPath).ShouldBeTrue();          // Skript wurde trotzdem exportiert
            lastStatus.ShouldNotBeNull();
            lastStatus!.ShouldContain("GDS skipped");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }
}

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.Views;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// User story: fork-on-edit of a bundled (foundry) component, driven entirely through the real
/// MainWindow — hover the library row, click its ✏, type into the code editor, click
/// "Save changes" — and verify the fork lands in the (temp) user-pdks root while the bundled
/// JSON stays byte-identical.
/// </summary>
[Trait("Category", "UiFlows")]
[Collection("LocalizationSingleton")]
[Trait("Category", "Slow")]
public class UiFlowEditForkTests
{
    private const string PdkName = "Demo PDK";
    private const string ComponentName = "1x2 MMI Splitter";

    [AvaloniaFact]
    public void EditBundledComponent_forkOnSave_throughRealClicks()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        var template = vm.LeftPanel.AllTemplates.Single(t => t.PdkSource == PdkName && t.Name == ComponentName);
        var bundledRow = vm.LeftPanel.PdkManager.LoadedPdks.Single(p => p.Name == PdkName);
        bundledRow.IsBundled.ShouldBeTrue("precondition: the Demo PDK loads as a bundled foundry PDK");
        var bundledPath = bundledRow.FilePath!;
        var bundledBytesBefore = File.ReadAllBytes(bundledPath);

        // ✏ on the library row (hover reveals it; Click moves the pointer first).
        host.LibraryListBox.ScrollIntoView(template);
        UiInput.RunJobs();
        UiInput.Click(win, UiInput.FindButton(win, "✏", template));

        var editors = win.OwnedWindows.OfType<NewComponentWindow>().ToList();
        editors.Count.ShouldBe(1, "the ✏ must open exactly one editor window");
        editors[0].Title.ShouldBe($"Edit Component: {ComponentName}");

        // A second ✏ click re-activates the existing editor instead of duplicating it.
        UiInput.Click(win, UiInput.FindButton(win, "✏", template));
        win.OwnedWindows.OfType<NewComponentWindow>().Count().ShouldBe(1,
            "a second ✏ click on the same component must not open a second window");

        var editor = win.OwnedWindows.OfType<NewComponentWindow>().Single();
        var editorVm = (NewComponentViewModel)editor.DataContext!;
        editorVm.IsEditMode.ShouldBeTrue();
        var loadedCode = editorVm.Code;

        // Real typing through AvaloniaEdit: click into the editor, jump to the document start
        // (Ctrl+Home — a center click would land the caret mid-code), prepend a comment line.
        var textEditor = UiInput.Descendants<AvaloniaEdit.TextEditor>(editor).First();
        UiInput.Click(editor, textEditor);
        UiInput.PressKey(editor, Key.Home, RawInputModifiers.Control);
        UiInput.TypeText(editor, "# tweaked via ui flow");
        UiInput.PressKey(editor, Key.Enter);
        editorVm.Code.ShouldStartWith("# tweaked via ui flow",
            customMessage: "typed keys must reach the view model through the editor binding");
        editorVm.Code.ShouldContain(loadedCode.Trim());

        UiInput.Click(editor, UiInput.FindButton(editor, "Save changes"));

        // Fork file in the TEMP user-pdks root; bundled JSON untouched byte-for-byte.
        var forkPath = host.UserPdkStore.ResolveNamedPath(PdkName);
        File.Exists(forkPath).ShouldBeTrue($"save must fork the PDK to {forkPath} (status: {editorVm.StatusText})");
        Path.GetFullPath(forkPath).ShouldStartWith(Path.GetFullPath(host.UserPdkRoot));
        File.ReadAllBytes(bundledPath).ShouldBe(bundledBytesBefore, "the bundled JSON must never be written");
        File.ReadAllText(forkPath).ShouldContain("# tweaked via ui flow");

        // Library switched to the editable copy: custom, restorable (✕), shadowing the bundled entry.
        var forked = vm.LeftPanel.AllTemplates.Single(t => t.PdkSource == PdkName && t.Name == ComponentName);
        forked.IsCustom.ShouldBeTrue();
        forked.IsDeletable.ShouldBeTrue("the edited copy must offer the Restore-Original ✕");
        var row = vm.LeftPanel.PdkManager.LoadedPdks.Single(p => p.Name == PdkName);
        row.IsBundled.ShouldBeFalse();
        row.ShadowsBundledPdk.ShouldBeTrue();

        win.OwnedWindows.OfType<NewComponentWindow>().ShouldBeEmpty("a successful save closes the editor");
    }
}

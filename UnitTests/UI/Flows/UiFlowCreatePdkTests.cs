using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.Views;
using Shouldly;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// User story: create a custom PDK from the PDK-Management "+" header button — type its name,
/// keep "Use existing process", pick a loaded process, click "Create PDK" — and verify the PDK
/// appears in the PDK-Management list with its file in the (temp) user-pdks root.
/// </summary>
[Trait("Category", "UiFlows")]
public class UiFlowCreatePdkTests
{
    private const string NewPdkName = "My Flow PDK";

    [AvaloniaFact]
    public void CreateCustomPdk_throughPlusButtonAndModalDialog()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        var plus = UiInput.Descendants<Button>(win).First(b =>
            "+".Equals(b.Content as string, StringComparison.Ordinal)
            && Equals(ToolTip.GetTip(b), "Create custom PDK…"));
        UiInput.Click(win, plus);

        var dialog = win.OwnedWindows.OfType<CreateCustomPdkWindow>().Single();
        var dialogVm = (CreateCustomPdkViewModel)dialog.DataContext!;
        dialogVm.AvailableProcesses.ShouldNotBeEmpty("bundled PDKs must contribute pickable processes");

        // PDK name — typed into the focused TextBox through the input pipeline.
        var nameBox = UiInput.Descendants<TextBox>(dialog).First(t => t.Watermark == "e.g. MyFoundryPDK");
        UiInput.Click(dialog, nameBox);
        UiInput.TypeText(dialog, NewPdkName);
        dialogVm.PdkName.ShouldBe(NewPdkName, "typed keys must reach the PdkName binding");

        // "Use existing process" is the default; pick the first loaded process by keyboard.
        // (Opening the ComboBox popup and clicking an item is not reliably hit-testable headless —
        // arrow keys on the closed ComboBox are the equivalent real keyboard interaction.)
        var processCombo = UiInput.Descendants<ComboBox>(dialog).First(c => c.IsEffectivelyVisible);
        processCombo.Focus();
        UiInput.RunJobs();
        UiInput.PressKey(dialog, Key.Down);
        dialogVm.SelectedExistingProcess.ShouldNotBeNull("Down on the ComboBox must select the first process");

        UiInput.Click(dialog, UiInput.FindButton(dialog, "Create PDK"));

        // The dialog closed itself via PdkCreated and the main window registered the PDK.
        win.OwnedWindows.OfType<CreateCustomPdkWindow>().ShouldBeEmpty(
            $"Create must close the dialog (status: {dialogVm.StatusText})");
        var row = vm.LeftPanel.PdkManager.LoadedPdks.SingleOrDefault(p => p.Name == NewPdkName);
        row.ShouldNotBeNull("the new PDK must appear in the PDK-Management list");
        row.IsBundled.ShouldBeFalse();
        row.FilePath.ShouldNotBeNull();
        File.Exists(row.FilePath).ShouldBeTrue();
        Path.GetFullPath(row.FilePath!).ShouldStartWith(Path.GetFullPath(host.UserPdkRoot));

        // The row is really rendered in the PDK-Management ItemsControl, not just in the VM.
        UiInput.Descendants<TextBlock>(win).ShouldContain(t => t.Text == NewPdkName);
    }
}

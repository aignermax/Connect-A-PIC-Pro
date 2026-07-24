using System.Text.RegularExpressions;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// XAML review lock for the field round-6 UX wish: the status/error text in the
/// "Edit Component" / "New Component" window must be COPYABLE, so users can paste a
/// failing Python error into a search or a bug report. Avalonia's plain
/// <c>TextBlock</c> is not selectable; the binding must sit on a
/// <c>SelectableTextBlock</c>.
/// </summary>
public class NewComponentWindowStatusTextTests
{
    [Fact]
    public void StatusText_IsRenderedAsSelectableTextBlock_soErrorsCanBeCopied()
    {
        var axaml = File.ReadAllText(FindWindowAxaml());

        var statusBindings = Regex.Matches(
            axaml, @"<(\w+)[^>]*\{Binding StatusText\}", RegexOptions.Singleline);

        statusBindings.Count.ShouldBeGreaterThan(0, "the window must display StatusText");
        foreach (Match match in statusBindings)
        {
            match.Groups[1].Value.ShouldBe("SelectableTextBlock",
                "error/status text must be selectable so users can copy it");
        }
    }

    private static string FindWindowAxaml()
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(NewComponentWindowStatusTextTests).Assembly.Location)!);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName, "CAP.Avalonia", "Views", "NewComponentWindow.axaml");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException("NewComponentWindow.axaml not found above test assembly");
    }
}

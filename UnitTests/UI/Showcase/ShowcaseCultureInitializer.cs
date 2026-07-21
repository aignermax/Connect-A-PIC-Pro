using System.Globalization;
using System.Runtime.CompilerServices;

namespace UnitTests.UI.Showcase;

/// <summary>
/// Pins the process culture to en-US for opt-in screenshot runs (<c>UI_SHOT_DIR</c> set)
/// BEFORE the Avalonia headless platform starts. Dispatcher/render jobs execute under
/// execution contexts captured at platform init, and those contexts restore the culture
/// captured back then — a culture switched later inside a test never reaches render-time
/// text formatting (canvas labels, NumericUpDown spinners, TextBox bindings). Normal test
/// runs (no <c>UI_SHOT_DIR</c>) keep the machine's culture untouched.
/// </summary>
internal static class ShowcaseCultureInitializer
{
    [ModuleInitializer]
    internal static void PinCultureForScreenshotRuns()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return;
        var english = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}

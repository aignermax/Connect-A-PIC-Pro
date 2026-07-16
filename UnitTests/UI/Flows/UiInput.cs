using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UnitTests.UI.Flows;

/// <summary>
/// Simulated-input helpers over <see cref="HeadlessWindowExtensions"/>: visual-tree lookup plus
/// real mouse/keyboard event dispatch through Avalonia's input pipeline (hit testing included).
/// Every action ends with <see cref="Dispatcher.UIThread.RunJobs"/> so bindings and async
/// command continuations settle before the next assertion — never sleep, always pump.
/// </summary>
internal static class UiInput
{
    public static void RunJobs() => Dispatcher.UIThread.RunJobs();

    public static IEnumerable<T> Descendants<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>();

    /// <summary>
    /// Finds a Button by its literal string Content, optionally scoped to the row whose
    /// DataContext is <paramref name="dataContext"/> (e.g. the ✏ of one library template).
    /// </summary>
    public static Button FindButton(Window window, string content, object? dataContext = null) =>
        Descendants<Button>(window).First(b =>
            content.Equals(b.Content as string, StringComparison.Ordinal)
            && (dataContext is null || ReferenceEquals(b.DataContext, dataContext)));

    /// <summary>Window-space point at the given relative position inside <paramref name="control"/>.</summary>
    public static Point PointIn(Window window, Visual control, double relX = 0.5, double relY = 0.5)
    {
        var local = new Point(control.Bounds.Width * relX, control.Bounds.Height * relY);
        var translated = control.TranslatePoint(local, window)
            ?? throw new InvalidOperationException($"{control.GetType().Name} is not attached to {window.Title}.");
        return translated;
    }

    /// <summary>
    /// Left-clicks the control through the window's input pipeline. Moves the pointer first so
    /// :pointerover styles (e.g. the library rows' hover-revealed ✏/✕ actions) apply like for
    /// a real user.
    /// </summary>
    public static void Click(Window window, Visual control, double relX = 0.5, double relY = 0.5) =>
        ClickAt(window, PointIn(window, control, relX, relY));

    public static void ClickAt(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left);
        RunJobs();
    }

    /// <summary>Sends text to the focused control (TextBox, AvaloniaEdit TextArea, …).</summary>
    public static void TypeText(Window window, string text)
    {
        window.KeyTextInput(text);
        RunJobs();
    }

    public static void PressKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers);
        window.KeyRelease(key, modifiers);
        RunJobs();
    }

    /// <summary>
    /// Drags the left mouse button from <paramref name="from"/> to <paramref name="to"/> with an
    /// intermediate move, matching how gesture recognizers see a real rubber-band drag.
    /// </summary>
    public static void DragMouse(Window window, Point from, Point to)
    {
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        window.MouseMove(mid, RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        RunJobs();
    }
}

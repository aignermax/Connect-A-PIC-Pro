using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels.ComponentSettings;

namespace CAP.Avalonia.Views;

/// <summary>
/// Code-behind for the Component Settings dialog window.
/// </summary>
public partial class ComponentSettingsDialog : Window
{
    /// <summary>Initialises the Component Settings dialog.</summary>
    public ComponentSettingsDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Cancels a running FDTD recompute when the dialog is closed — otherwise the
    /// solve (and its Docker container) would keep running in the background.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ComponentSettingsDialogViewModel vm)
            vm.CancelRecalculate();
        base.OnClosing(e);
    }

    /// <summary>
    /// Fires the per-instance Nazca code editor's async source load once the dialog
    /// is visible (issue #556). Fire-and-forget so the dialog opens immediately; the
    /// editor populates as soon as the (cached) module-mode render returns. The VM's
    /// InitializeAsync is crash-proof, so a swallowed continuation is safe.
    /// </summary>
    private void OnLoaded(object? sender, EventArgs e)
    {
        if (DataContext is not ComponentSettingsDialogViewModel vm)
            return;
        var editor = vm.NazcaCodeEditor;
        if (editor == null)
            return;

        // RenderAsync marshals back onto the captured UI context; observable-property
        // setters in InitializeAsync therefore run on the UI thread.
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { await editor.InitializeAsync(); }
            catch { /* InitializeAsync is crash-proof; this is belt-and-braces. */ }
        });
    }

    /// <summary>
    /// Opens the reference docs of the currently selected override backend
    /// (Nazca manual or gdsfactory docs, issues #556/#637) in the default browser.
    /// </summary>
    private void OnOpenNazcaDocs(object? sender, RoutedEventArgs e)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher == null)
            return;
        var url = (DataContext as ComponentSettingsDialogViewModel)?.NazcaCodeEditor?.BackendTexts.DocsUrl
            ?? ViewModels.ComponentSettings.InstanceOverride.OverrideBackendTexts.Nazca.DocsUrl;
        _ = launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>Copies the current preview error to the clipboard so it can be pasted into a report.</summary>
    private void OnCopyError(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComponentSettingsDialogViewModel vm)
            return;
        var error = vm.NazcaCodeEditor?.PreviewError;
        if (string.IsNullOrEmpty(error))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            _ = clipboard.SetTextAsync(error);
    }
}

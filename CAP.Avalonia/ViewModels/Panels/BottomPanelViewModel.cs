using CommunityToolkit.Mvvm.ComponentModel;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP_Core;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// ViewModel for the bottom panel.
/// Contains connection routing, element locking, status text, and error console.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class BottomPanelViewModel : ObservableObject
{
    /// <summary>
    /// ViewModel for per-connection routing options (style, width/radius, freeze — issue #574).
    /// </summary>
    public ConnectionRoutingViewModel ConnectionRouting { get; }

    /// <summary>
    /// ViewModel for re-routing imported (frozen) waveguide routes on demand (issue #857).
    /// </summary>
    public Canvas.RerouteImported.RerouteImportedRoutesViewModel RerouteImported { get; }

    /// <summary>
    /// ViewModel for locking/unlocking components and connections.
    /// </summary>
    public ElementLockViewModel ElementLock { get; }

    /// <summary>
    /// ViewModel for the collapsible error console panel.
    /// </summary>
    public ErrorConsoleViewModel ErrorConsole { get; }

    /// <summary>
    /// ViewModel for the collapsible transient/eye-diagram analysis dock (#570/#535).
    /// </summary>
    public AnalysisDockViewModel Analysis { get; }

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Status.Ready");

    /// <summary>
    /// Initializes the bottom panel with injected sub-ViewModels.
    /// </summary>
    public BottomPanelViewModel(
        DesignCanvasViewModel canvas,
        CommandManager commandManager,
        ConnectionRoutingViewModel connectionRouting,
        Canvas.RerouteImported.RerouteImportedRoutesViewModel rerouteImported,
        ElementLockViewModel elementLock,
        ErrorConsoleViewModel errorConsole,
        AnalysisDockViewModel analysis)
    {
        ConnectionRouting = connectionRouting;
        RerouteImported = rerouteImported;
        ElementLock = elementLock;
        ErrorConsole = errorConsole;
        Analysis = analysis;

        // Configure ViewModels that need canvas and command manager
        ElementLock.Configure(canvas, commandManager);
        Analysis.Configure(canvas);
    }

    /// <summary>
    /// Updates the status text display.
    /// </summary>
    public void SetStatus(string status)
    {
        StatusText = status;
    }
}

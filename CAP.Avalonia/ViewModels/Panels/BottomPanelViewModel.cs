using CommunityToolkit.Mvvm.ComponentModel;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Commands;
using CAP_Core;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// ViewModel for the bottom panel.
/// Contains waveguide length configuration, element locking, status text, and error console.
/// Max 250 lines per CLAUDE.md guideline.
/// </summary>
public partial class BottomPanelViewModel : ObservableObject
{
    /// <summary>
    /// ViewModel for parameterized waveguide length configuration (phase matching).
    /// </summary>
    public WaveguideLengthViewModel WaveguideLength { get; }

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
    private string _statusText = "Ready";

    /// <summary>
    /// Initializes the bottom panel with injected sub-ViewModels.
    /// </summary>
    public BottomPanelViewModel(
        DesignCanvasViewModel canvas,
        CommandManager commandManager,
        WaveguideLengthViewModel waveguideLength,
        ElementLockViewModel elementLock,
        ErrorConsoleViewModel errorConsole,
        AnalysisDockViewModel analysis)
    {
        WaveguideLength = waveguideLength;
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

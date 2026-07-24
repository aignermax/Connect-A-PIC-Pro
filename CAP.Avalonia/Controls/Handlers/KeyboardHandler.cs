using Avalonia;
using Avalonia.Input;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.Controls.Handlers;

/// <summary>
/// Handles keyboard input for the design canvas.
/// All key-to-command mappings are centralized here for testability.
/// </summary>
public sealed class KeyboardHandler
{
    private readonly Func<DesignCanvasViewModel?> _getViewModel;
    private readonly Func<MainViewModel?> _getMainViewModel;
    private readonly Func<Rect> _getBounds;

    /// <summary>
    /// Initializes a new instance of <see cref="KeyboardHandler"/>.
    /// </summary>
    /// <param name="getViewModel">Callback to retrieve the current canvas ViewModel.</param>
    /// <param name="getMainViewModel">Callback to retrieve the main ViewModel.</param>
    /// <param name="getBounds">Callback to retrieve the canvas bounds (for zoom-to-fit).</param>
    public KeyboardHandler(
        Func<DesignCanvasViewModel?> getViewModel,
        Func<MainViewModel?> getMainViewModel,
        Func<Rect> getBounds)
    {
        _getViewModel = getViewModel;
        _getMainViewModel = getMainViewModel;
        _getBounds = getBounds;
    }

    /// <summary>
    /// Processes a key-down event and dispatches to the appropriate command.
    /// </summary>
    /// <param name="e">The keyboard event arguments.</param>
    public void OnKeyDown(KeyEventArgs e)
    {
        var mainVm = _getMainViewModel();
        if (mainVm == null) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.S:
                HandleSKey(ctrl, mainVm);
                e.Handled = true;
                break;
            case Key.C when !ctrl:
                mainVm.SetConnectModeCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D when !ctrl:
                mainVm.SetDeleteModeCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                mainVm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                HandleEscapeKey(mainVm);
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                mainVm.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                mainVm.RedoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.R when !ctrl:
                mainVm.RotateSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.G when !ctrl:
                HandleGKey(e);
                e.Handled = true;
                break;
            case Key.F when !ctrl:
                var bounds = _getBounds();
                mainVm.ZoomToFit(bounds.Width, bounds.Height);
                e.Handled = true;
                break;
            case Key.P when !ctrl:
                HandlePKey(mainVm);
                e.Handled = true;
                break;
            case Key.L when ctrl:
                mainVm.BottomPanel.ElementLock.ToggleSelectedComponentsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.L:
                mainVm.RunSimulationCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V when !ctrl:
                e.Handled = true;
                break;
        }
    }

    private void HandleSKey(bool ctrlPressed, MainViewModel mainVm)
    {
        if (ctrlPressed)
            mainVm.SaveDesignCommand.Execute(null);
        else
            mainVm.SetSelectModeCommand.Execute(null);
    }

    private void HandleGKey(KeyEventArgs e)
    {
        var vm = _getViewModel();
        var mainVm = _getMainViewModel();
        if (vm == null) return;

        bool shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (shiftPressed)
        {
            vm.ShowGridOverlay = !vm.ShowGridOverlay;
        }
        else
        {
            vm.GridSnap.Toggle();
            if (mainVm != null)
            {
                mainVm.StatusText = vm.GridSnap.IsEnabled
                    ? $"Grid snap ON ({vm.GridSnap.GridSizeMicrometers}µm)"
                    : "Grid snap OFF";
            }
        }
    }

    private void HandlePKey(MainViewModel mainVm)
    {
        var vm = _getViewModel();
        if (vm == null) return;

        if (!vm.ShowPowerFlow)
        {
            if (vm.PowerFlowVisualizer.CurrentResult == null)
                mainVm.RunSimulationCommand.Execute(null);
            else
            {
                vm.ShowPowerFlow = true;
                vm.PowerFlowVisualizer.IsEnabled = true;
            }
            mainVm.StatusText = LocalizationService.Instance.Translate("Overlay.PowerFlowOn");
        }
        else
        {
            vm.ShowPowerFlow = false;
            vm.PowerFlowVisualizer.IsEnabled = false;
            mainVm.StatusText = LocalizationService.Instance.Translate("Overlay.PowerFlowOff");
        }
    }

    private void HandleEscapeKey(MainViewModel mainVm)
    {
        var vm = _getViewModel();
        if (vm != null && vm.IsInGroupEditMode)
        {
            if (mainVm.CommandManager != null && vm.CurrentEditGroup != null)
            {
                var cmd = new ExitGroupEditModeCommand(vm, vm.CurrentEditGroup);
                mainVm.CommandManager.ExecuteCommand(cmd);
            }
            else
            {
                vm.ExitGroupEditMode();
            }
            mainVm.StatusText = LocalizationService.Instance.Translate("Status.ExitedGroupEditMode");
        }
        else
        {
            mainVm.SetSelectModeCommand.Execute(null);
        }
    }
}

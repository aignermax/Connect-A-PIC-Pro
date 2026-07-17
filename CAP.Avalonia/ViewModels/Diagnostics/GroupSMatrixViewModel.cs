using System.Collections.ObjectModel;
using CAP_Core.Components.Core;
using CAP_Core.Grid;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for displaying ComponentGroup S-Matrix computation status and diagnostics.
/// Shows which groups have valid S-Matrices and which need recomputation.
/// </summary>
public partial class GroupSMatrixViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private ObservableCollection<GroupMatrixInfo> _groupMatrices = new();

    [ObservableProperty]
    private bool _hasGroups;

    private GridManager? _grid;

    /// <summary>
    /// Updates the diagnostics display based on the current grid state.
    /// </summary>
    public void UpdateFromGrid(GridManager? grid)
    {
        _grid = grid;
        GroupMatrices.Clear();

        if (grid == null)
        {
            StatusText = LocalizationService.Instance.Translate("Diag.Group.NoGrid");
            HasGroups = false;
            return;
        }

        var allComponents = grid.TileManager.GetAllComponents();
        var groups = allComponents.OfType<ComponentGroup>().ToList();

        if (groups.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Diag.Group.NoGroups");
            HasGroups = false;
            return;
        }

        HasGroups = true;

        foreach (var group in groups)
        {
            var info = new GroupMatrixInfo
            {
                GroupName = group.GroupName,
                ChildCount = group.ChildComponents.Count,
                InternalPathCount = group.InternalPaths.Count,
                ExternalPinCount = group.ExternalPins.Count,
                HasSMatrix = group.WaveLengthToSMatrixMap.Count > 0,
                WavelengthCount = group.WaveLengthToSMatrixMap.Count
            };

            GroupMatrices.Add(info);
        }

        int validCount = GroupMatrices.Count(g => g.HasSMatrix);
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Diag.Group.SummaryFormat"), validCount, groups.Count);
    }

    /// <summary>
    /// Computes S-Matrices for all groups in the current design.
    /// </summary>
    [RelayCommand]
    private void ComputeAllGroupMatrices()
    {
        if (_grid == null)
            return;

        var allComponents = _grid.TileManager.GetAllComponents();
        var groups = allComponents.OfType<ComponentGroup>().ToList();

        foreach (var group in groups)
        {
            if (group.ExternalPins.Count > 0)
            {
                group.ComputeSMatrix();
            }
        }

        UpdateFromGrid(_grid);
    }

    /// <summary>
    /// Clears all computed S-Matrices (for testing/debugging).
    /// </summary>
    [RelayCommand]
    private void ClearAllGroupMatrices()
    {
        if (_grid == null)
            return;

        var allComponents = _grid.TileManager.GetAllComponents();
        var groups = allComponents.OfType<ComponentGroup>().ToList();

        foreach (var group in groups)
        {
            group.WaveLengthToSMatrixMap.Clear();
        }

        UpdateFromGrid(_grid);
    }
}

/// <summary>
/// Information about a single ComponentGroup's S-Matrix status.
/// </summary>
public class GroupMatrixInfo
{
    public string GroupName { get; set; } = "";
    public int ChildCount { get; set; }
    public int InternalPathCount { get; set; }
    public int ExternalPinCount { get; set; }
    public bool HasSMatrix { get; set; }
    public int WavelengthCount { get; set; }

    public string Status => HasSMatrix
        ? string.Format(LocalizationService.Instance.Translate("Diag.Group.WavelengthsFormat"), WavelengthCount)
        : ExternalPinCount > 0
            ? LocalizationService.Instance.Translate("Diag.Group.NotComputed")
            : LocalizationService.Instance.Translate("Diag.Group.NoExternalPins");
}

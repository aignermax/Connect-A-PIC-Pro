using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components.Core;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Hierarchy;

/// <summary>
/// Manages the hierarchy tree view panel showing component structure.
/// Displays a Figma-style tree with expand/collapse controls for ComponentGroups.
/// Synchronizes selection with the canvas and supports navigation.
/// </summary>
public partial class HierarchyPanelViewModel : ObservableObject
{
    /// <summary>
    /// Root-level nodes in the hierarchy (top-level components and groups).
    /// </summary>
    public ObservableCollection<HierarchyNodeViewModel> RootNodes { get; } = new();

    /// <summary>
    /// Reference to the canvas ViewModel for monitoring components and connections.
    /// </summary>
    private readonly DesignCanvasViewModel _canvas;

    /// <summary>
    /// Callback to navigate the canvas to a specific position (zoom to component).
    /// Set by MainViewModel after initialization.
    /// </summary>
    public Action<double, double>? NavigateToPosition { get; set; }

    /// <summary>
    /// Callback to get the viewport size for zoom calculations.
    /// </summary>
    public Func<(double width, double height)>? GetViewportSize { get; set; }

    /// <summary>
    /// Callback to handle component rename, typically wired to the undo-aware command manager.
    /// When null, the rename is applied directly without undo support.
    /// </summary>
    public Action<Component, string>? RenameComponent { get; set; }

    /// <summary>
    /// Callback invoked when the user requests "Component Settings…" for a hierarchy node.
    /// Set by <see cref="CAP.Avalonia.ViewModels.MainViewModel"/> after initialization.
    /// </summary>
    public Action<HierarchyNodeViewModel>? OpenComponentSettings { get; set; }

    /// <summary>
    /// Returns <c>true</c> when a per-instance S-matrix override exists for the given
    /// component identifier. Wired to <c>FileOperationsViewModel.StoredSMatrices</c>
    /// by <see cref="CAP.Avalonia.Views.MainWindow"/>.
    /// </summary>
    public Func<string, bool>? CheckHasSMatrixOverride { get; set; }

    /// <summary>
    /// Guards against re-entrant selection sync when hierarchy triggers a canvas update.
    /// </summary>
    private bool _suppressSync;

    public HierarchyPanelViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

        // Subscribe to canvas changes
        _canvas.Components.CollectionChanged += (s, e) => RebuildTree();

        // Canvas → Hierarchy: mirror SelectedComponent changes into the hierarchy tree.
        _canvas.PropertyChanged += OnCanvasPropertyChanged;
    }

    /// <summary>
    /// Responds to <see cref="DesignCanvasViewModel.SelectedComponent"/> changes
    /// so that clicking a component on the canvas automatically highlights its node.
    /// </summary>
    private void OnCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesignCanvasViewModel.SelectedComponent) && !_suppressSync)
            SyncSelectionFromCanvas(_canvas.SelectedComponent);
    }

    /// <summary>
    /// Rebuilds the entire hierarchy tree from the canvas components.
    /// Called when components are added, removed, or grouped/ungrouped.
    /// </summary>
    public void RebuildTree()
    {
        RootNodes.Clear();

        // Build tree from canvas components (only top-level components, not children of groups)
        foreach (var compVm in _canvas.Components)
        {
            // Skip components that are children of a group (they'll be added recursively)
            // UNLESS we're in group edit mode - then children of the current edit group should appear as root nodes
            if (compVm.Component.ParentGroup != null && compVm.Component.ParentGroup != _canvas.CurrentEditGroup)
                continue;

            var node = CreateNodeRecursive(compVm.Component, compVm);
            RootNodes.Add(node);
        }
    }

    /// <summary>
    /// Updates the <see cref="HierarchyNodeViewModel.HasSMatrixOverride"/> flag on every
    /// node in the tree. Call this after an S-matrix import or deletion so the 📊 badge
    /// in the hierarchy panel appears or disappears immediately.
    /// </summary>
    public void RefreshOverrideMarkers()
    {
        foreach (var root in RootNodes)
            RefreshOverrideMarkersRecursive(root);
    }

    private void RefreshOverrideMarkersRecursive(HierarchyNodeViewModel node)
    {
        node.HasSMatrixOverride = CheckHasSMatrixOverride?.Invoke(node.Component.Identifier) ?? false;
        foreach (var child in node.Children)
            RefreshOverrideMarkersRecursive(child);
    }

    /// <summary>
    /// Recursively creates a hierarchy node and its children.
    /// </summary>
    private HierarchyNodeViewModel CreateNodeRecursive(Component component, ComponentViewModel? componentVm)
    {
        var node = new HierarchyNodeViewModel(component)
        {
            ComponentViewModel = componentVm,
            FocusRequested = FocusOnComponent,
            SelectionRequested = SelectComponent,
            RenameConfirmed = (n, newName) => ApplyRename(n.Component, newName),
            OpenSettingsRequested = n => OpenComponentSettings?.Invoke(n),
            HasSMatrixOverride = CheckHasSMatrixOverride?.Invoke(component.Identifier) ?? false
        };

        // If this is a group, recursively add its children
        if (component is ComponentGroup group)
        {
            foreach (var childComp in group.ChildComponents)
            {
                // Find the ComponentViewModel for this child
                var childVm = _canvas.Components.FirstOrDefault(c => c.Component == childComp);
                var childNode = CreateNodeRecursive(childComp, childVm);
                node.Children.Add(childNode);
            }
        }

        return node;
    }

    /// <summary>
    /// Synchronizes hierarchy selection with canvas selection.
    /// Called when a component is selected on the canvas.
    /// </summary>
    public void SyncSelectionFromCanvas(ComponentViewModel? selectedComponent)
    {
        // Clear all selections in the tree
        ClearAllSelections();

        if (selectedComponent == null)
            return;

        // Find and select the corresponding node
        var node = FindNodeByComponent(selectedComponent.Component);
        if (node != null)
        {
            node.IsSelected = true;
            ExpandParentsToNode(node);
        }
    }

    /// <summary>
    /// Synchronizes edit mode highlighting with canvas edit state.
    /// Called when entering/exiting group edit mode.
    /// </summary>
    public void SyncEditModeFromCanvas(ComponentGroup? editGroup)
    {
        // Clear all edit mode flags
        ClearAllEditModeFlags();

        if (editGroup == null)
            return;

        // Find and highlight the edited group node
        var node = FindNodeByComponent(editGroup);
        if (node != null)
        {
            node.IsInEditMode = true;
            ExpandParentsToNode(node);
        }
    }

    /// <summary>
    /// Clears all edit mode flags in the hierarchy tree.
    /// </summary>
    private void ClearAllEditModeFlags()
    {
        foreach (var rootNode in RootNodes)
        {
            ClearEditModeFlagsRecursive(rootNode);
        }
    }

    /// <summary>
    /// Recursively clears edit mode flags in a subtree.
    /// </summary>
    private void ClearEditModeFlagsRecursive(HierarchyNodeViewModel node)
    {
        node.IsInEditMode = false;
        foreach (var child in node.Children)
        {
            ClearEditModeFlagsRecursive(child);
        }
    }

    /// <summary>
    /// Finds a node in the tree by its component reference.
    /// </summary>
    private HierarchyNodeViewModel? FindNodeByComponent(Component component)
    {
        foreach (var rootNode in RootNodes)
        {
            var found = rootNode.FindNodeByComponent(component);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Expands all parent nodes to make a node visible.
    /// </summary>
    private void ExpandParentsToNode(HierarchyNodeViewModel node)
    {
        // Since we don't track parent references, we'll expand all groups containing the component
        // This is a simplified approach - could be optimized with parent tracking
        foreach (var rootNode in RootNodes)
        {
            ExpandIfContains(rootNode, node.Component);
        }
    }

    /// <summary>
    /// Recursively expands nodes that contain the target component.
    /// </summary>
    private bool ExpandIfContains(HierarchyNodeViewModel node, Component target)
    {
        if (node.Component == target)
            return true;

        foreach (var child in node.Children)
        {
            if (ExpandIfContains(child, target))
            {
                node.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears all selections in the hierarchy tree.
    /// </summary>
    private void ClearAllSelections()
    {
        foreach (var rootNode in RootNodes)
        {
            ClearSelectionsRecursive(rootNode);
        }
    }

    /// <summary>
    /// Recursively clears selections in a subtree.
    /// </summary>
    private void ClearSelectionsRecursive(HierarchyNodeViewModel node)
    {
        node.IsSelected = false;
        foreach (var child in node.Children)
        {
            ClearSelectionsRecursive(child);
        }
    }

    /// <summary>
    /// Handles focus request from a hierarchy node (select and zoom canvas to component).
    /// Selects the component on canvas and pans the view to center it.
    /// </summary>
    private void FocusOnComponent(HierarchyNodeViewModel node)
    {
        if (node.Component == null) return;

        // First, select the component on the canvas
        SelectComponent(node);

        // Calculate center of component
        double centerX = node.Component.PhysicalX + node.Component.WidthMicrometers / 2;
        double centerY = node.Component.PhysicalY + node.Component.HeightMicrometers / 2;

        // Navigate to this position
        NavigateToPosition?.Invoke(centerX, centerY);
    }

    /// <summary>
    /// Handles selection request from a hierarchy node (select on canvas).
    /// Sets <see cref="_suppressSync"/> to avoid the canvas PropertyChanged listener
    /// re-entering this path and causing an infinite loop.
    /// </summary>
    private void SelectComponent(HierarchyNodeViewModel node)
    {
        if (node.ComponentViewModel == null) return;
        // Already the canvas selection — nothing to push. This value-equality guard is
        // what stops the node.IsSelected → SelectionRequested → canvas → node.IsSelected
        // cycle from recursing.
        if (_canvas.SelectedComponent == node.ComponentViewModel) return;

        _suppressSync = true;
        try
        {
            // Clear all canvas selections
            _canvas.Selection.ClearSelection();
            foreach (var comp in _canvas.Components)
                comp.IsSelected = false;

            // Select the component on canvas
            node.ComponentViewModel.IsSelected = true;
            _canvas.Selection.SelectedComponents.Add(node.ComponentViewModel);
            _canvas.SelectedComponent = node.ComponentViewModel;

            // Update hierarchy selection and expand parents so the node is visible
            ClearAllSelections();
            node.IsSelected = true;
            ExpandParentsToNode(node);
        }
        finally
        {
            _suppressSync = false;
        }
    }

    /// <summary>
    /// Updates a node's display after its content changes (e.g., children added/removed).
    /// </summary>
    public void RefreshNode(Component component)
    {
        var node = FindNodeByComponent(component);
        node?.RefreshDisplayName();
    }

    /// <summary>
    /// Applies a rename to the given component, using the <see cref="RenameComponent"/> callback
    /// when set (for undo support), or falling back to a direct rename.
    /// Always refreshes the node display afterward.
    /// </summary>
    private void ApplyRename(Component component, string newName)
    {
        if (RenameComponent != null)
        {
            RenameComponent(component, newName);
        }
        else
        {
            // Direct rename (no undo support) — used in tests and when no CommandManager is wired
            if (component is ComponentGroup group)
                group.GroupName = newName;
            else
                component.HumanReadableName = newName;
        }

        // Refresh display for regular components (groups auto-refresh via PropertyChanged)
        if (component is not ComponentGroup)
            RefreshNode(component);
    }
}

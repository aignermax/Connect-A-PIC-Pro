using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Components.Creation;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// ViewModel for managing the component library including saved ComponentGroup templates.
/// Handles user groups and PDK macro groups separately for easy filtering.
/// </summary>
public partial class ComponentLibraryViewModel : ObservableObject
{
    private readonly GroupLibraryManager _libraryManager;

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Library.NoGroupsLoaded");

    /// <summary>
    /// User-created group templates (wrapped for UI interactions).
    /// </summary>
    public ObservableCollection<GroupTemplateItemViewModel> UserGroups { get; } = new();

    /// <summary>
    /// PDK-provided group templates (macros, wrapped for UI interactions).
    /// </summary>
    public ObservableCollection<GroupTemplateItemViewModel> PdkGroups { get; } = new();

    /// <summary>
    /// Currently selected group template for drag-and-drop.
    /// </summary>
    [ObservableProperty]
    private GroupTemplate? _selectedGroupTemplate;

    /// <summary>
    /// Initializes the component library ViewModel.
    /// </summary>
    /// <param name="libraryManager">The group library manager service.</param>
    public ComponentLibraryViewModel(GroupLibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        LoadGroups();
    }

    /// <summary>
    /// Loads all group templates from the library.
    /// </summary>
    [RelayCommand]
    private void LoadGroups()
    {
        UserGroups.Clear();
        PdkGroups.Clear();

        _libraryManager.LoadTemplates();

        foreach (var template in _libraryManager.UserTemplates)
        {
            UserGroups.Add(new GroupTemplateItemViewModel(template, this));
        }

        foreach (var template in _libraryManager.PdkTemplates)
        {
            PdkGroups.Add(new GroupTemplateItemViewModel(template, this));
        }

        UpdateStatus();
    }

    /// <summary>
    /// Adds a new group template to the library.
    /// </summary>
    /// <param name="template">The template to add.</param>
    public void AddTemplate(GroupTemplate template)
    {
        var itemVm = new GroupTemplateItemViewModel(template, this);

        if (template.Source == "User")
        {
            UserGroups.Add(itemVm);
        }
        else
        {
            PdkGroups.Add(itemVm);
        }

        UpdateStatus();
    }

    /// <summary>
    /// Removes a group template from the library.
    /// </summary>
    /// <param name="template">The template to remove.</param>
    [RelayCommand]
    public void RemoveTemplate(GroupTemplate template)
    {
        if (_libraryManager.RemoveTemplate(template))
        {
            // Remove from wrapped collections by comparing FilePath since objects may differ after reload
            var userItem = UserGroups.FirstOrDefault(vm =>
                vm.Template == template ||
                (vm.Template.FilePath != null && vm.Template.FilePath == template.FilePath));
            if (userItem != null)
            {
                UserGroups.Remove(userItem);
            }

            var pdkItem = PdkGroups.FirstOrDefault(vm =>
                vm.Template == template ||
                (vm.Template.FilePath != null && vm.Template.FilePath == template.FilePath));
            if (pdkItem != null)
            {
                PdkGroups.Remove(pdkItem);
            }

            UpdateStatus();
        }
    }

    /// <summary>
    /// Callback to show a rename input dialog. Set by the View layer.
    /// Takes the current template name, returns new name or null if cancelled.
    /// </summary>
    public Func<string, Task<string?>>? ShowRenameDialogAsync { get; set; }

    /// <summary>
    /// Renames a group template using the ShowRenameDialogAsync callback.
    /// </summary>
    /// <param name="template">The template to rename.</param>
    [RelayCommand]
    private async Task RenameTemplate(GroupTemplate template)
    {
        if (ShowRenameDialogAsync == null)
            return;

        var newName = await ShowRenameDialogAsync(template.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == template.Name)
            return;

        newName = newName.Trim();
        var group = template.TemplateGroup;
        if (group == null)
            return;

        var source = template.Source;
        var description = template.Description;
        var preview = template.PreviewThumbnailBase64;

        _libraryManager.RemoveTemplate(template);

        var userItem = UserGroups.FirstOrDefault(vm => vm.Template == template);
        if (userItem != null)
            UserGroups.Remove(userItem);

        var pdkItem = PdkGroups.FirstOrDefault(vm => vm.Template == template);
        if (pdkItem != null)
            PdkGroups.Remove(pdkItem);

        var newTemplate = _libraryManager.SaveTemplate(group, newName, description, source);
        if (preview != null)
            newTemplate.PreviewThumbnailBase64 = preview;

        AddTemplate(newTemplate);
    }

    /// <summary>
    /// Duplicates a group template with a new name.
    /// </summary>
    /// <param name="template">The template to duplicate.</param>
    [RelayCommand]
    private void DuplicateTemplate(GroupTemplate template)
    {
        if (template.TemplateGroup == null)
            return;

        var duplicateName = $"{template.Name}_Copy";
        var duplicated = _libraryManager.SaveTemplate(
            template.TemplateGroup,
            duplicateName,
            template.Description,
            template.Source);

        AddTemplate(duplicated);
    }

    /// <summary>
    /// Updates the status text based on loaded templates.
    /// </summary>
    private void UpdateStatus()
    {
        // Only the empty-state hint is worth a line: when groups exist, the
        // "User Groups" / "PDK Macros" sections below already show the counts,
        // so a separate summary line is just redundant vertical space.
        StatusText = UserGroups.Count + PdkGroups.Count == 0 ? LocalizationService.Instance.Translate("Library.NoSavedGroups") : "";
    }

    /// <summary>
    /// Gets the underlying library manager for advanced operations.
    /// </summary>
    public GroupLibraryManager GetLibraryManager() => _libraryManager;
}

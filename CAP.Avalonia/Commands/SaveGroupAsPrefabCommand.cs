using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Commands;

/// <summary>
/// Command to save a ComponentGroup as a reusable prefab/template in the library.
/// This is the explicit user action required to make groups appear in "Saved Groups" panel.
/// </summary>
public class SaveGroupAsPrefabCommand : IUndoableCommand
{
    private readonly ComponentLibraryViewModel _libraryViewModel;
    private readonly GroupPreviewGenerator _previewGenerator;
    private readonly ComponentGroup _group;
    private readonly string _name;
    private readonly string? _description;
    private readonly Func<string, string?>? _nazcaOverrideJsonProvider;
    private CAP_Core.Components.Creation.GroupTemplate? _createdTemplate;

    /// <summary>
    /// Initializes the save-as-prefab command.
    /// </summary>
    /// <param name="nazcaOverrideJsonProvider">
    /// Optional lookup returning the serialized Nazca override for a member component
    /// identifier, so raw-code/override geometry travels with the template (issue #720).
    /// </param>
    public SaveGroupAsPrefabCommand(
        ComponentLibraryViewModel libraryViewModel,
        GroupPreviewGenerator previewGenerator,
        ComponentGroup group,
        string name,
        string? description = null,
        Func<string, string?>? nazcaOverrideJsonProvider = null)
    {
        _libraryViewModel = libraryViewModel;
        _previewGenerator = previewGenerator;
        _group = group;
        _name = name;
        _description = description;
        _nazcaOverrideJsonProvider = nazcaOverrideJsonProvider;
    }

    public string Description => $"Save '{_name}' as prefab";

    public void Execute()
    {
        // Mark group as prefab (done by GroupLibraryManager.SaveTemplate)
        var libraryManager = _libraryViewModel.GetLibraryManager();
        _createdTemplate = libraryManager.SaveTemplate(
            _group, _name, _description, "User", _nazcaOverrideJsonProvider);

        // Generate preview thumbnail
        var previewBase64 = _previewGenerator.GeneratePreview(_group);
        if (previewBase64 != null)
        {
            _createdTemplate.PreviewThumbnailBase64 = previewBase64;
        }

        // Add to ViewModel collection to update UI
        _libraryViewModel.AddTemplate(_createdTemplate);
    }

    public void Undo()
    {
        if (_createdTemplate == null)
            return;

        // Remove from library (this also sets IsPrefab = false)
        _group.IsPrefab = false;
        _libraryViewModel.RemoveTemplateCommand.Execute(_createdTemplate);
        _createdTemplate = null;
    }
}

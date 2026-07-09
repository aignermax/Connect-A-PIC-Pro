using System.Text.Json;
using CAP_Core.Components.Core;

namespace CAP_Core.Components.Creation;

/// <summary>
/// Manages the library of saved ComponentGroup templates.
/// Handles loading, saving, and instantiation of group templates.
/// </summary>
public class GroupLibraryManager
{
    private const string DefaultLibraryFolder = "GroupLibrary";
    private const string UserGroupsSubfolder = "UserGroups";
    private const string PdkGroupsSubfolder = "PdkGroups";

    private readonly string _libraryBasePath;
    private readonly List<GroupTemplate> _templates = new();

    /// <summary>
    /// All loaded group templates (user + PDK).
    /// </summary>
    public IReadOnlyList<GroupTemplate> Templates => _templates;

    /// <summary>
    /// User-created group templates.
    /// </summary>
    public IEnumerable<GroupTemplate> UserTemplates =>
        _templates.Where(t => t.Source == "User");

    /// <summary>
    /// PDK-provided group templates (macros).
    /// </summary>
    public IEnumerable<GroupTemplate> PdkTemplates =>
        _templates.Where(t => t.Source == "PDK");

    /// <summary>
    /// Initializes the group library manager.
    /// </summary>
    /// <param name="basePath">Base path for library storage. If null, uses application data folder.</param>
    public GroupLibraryManager(string? basePath = null)
    {
        _libraryBasePath = basePath ?? GetDefaultLibraryPath();
        EnsureLibraryDirectoriesExist();
    }

    /// <summary>
    /// Loads all group templates from the library folders.
    /// </summary>
    public void LoadTemplates()
    {
        _templates.Clear();

        // Load user groups
        LoadTemplatesFromFolder(
            Path.Combine(_libraryBasePath, UserGroupsSubfolder),
            "User");

        // Load PDK groups
        LoadTemplatesFromFolder(
            Path.Combine(_libraryBasePath, PdkGroupsSubfolder),
            "PDK");
    }

    /// <summary>
    /// Saves a ComponentGroup as a template to the library.
    /// Marks the group as a prefab (IsPrefab = true).
    /// </summary>
    /// <param name="group">The group to save.</param>
    /// <param name="name">Display name for the template.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="source">Source category ("User" or "PDK").</param>
    /// <returns>The created GroupTemplate.</returns>
    public GroupTemplate SaveTemplate(
        ComponentGroup group,
        string name,
        string? description = null,
        string source = "User")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Template name cannot be empty.", nameof(name));

        // Mark group as prefab
        group.IsPrefab = true;

        // Determine target folder
        var targetFolder = source == "User"
            ? Path.Combine(_libraryBasePath, UserGroupsSubfolder)
            : Path.Combine(_libraryBasePath, PdkGroupsSubfolder);

        // Generate unique filename
        var safeName = MakeSafeFilename(name);
        var fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(targetFolder, fileName);

        // Serialize full group data so it can be reconstructed when loaded from disk
        var groupDataJson = GroupTemplateSerializer.Serialize(group);

        var fileData = new GroupLibraryFileData
        {
            Name = name,
            Description = description ?? "",
            Source = source,
            CreatedAt = DateTime.Now,
            ComponentCount = group.ChildComponents.Count,
            WidthMicrometers = group.WidthMicrometers,
            HeightMicrometers = group.HeightMicrometers,
            GroupData = groupDataJson
        };

        var json = JsonSerializer.Serialize(fileData, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(filePath, json);

        // Create template
        var template = new GroupTemplate
        {
            Name = name,
            Description = description ?? "",
            Category = source == "User" ? "User Groups" : "PDK Macros",
            FilePath = filePath,
            ComponentCount = group.ChildComponents.Count,
            WidthMicrometers = group.WidthMicrometers,
            HeightMicrometers = group.HeightMicrometers,
            Source = source,
            TemplateGroup = group,
            CreatedAt = DateTime.Now
        };

        _templates.Add(template);
        return template;
    }

    /// <summary>
    /// Removes a template from the library and deletes its file.
    /// </summary>
    /// <param name="template">The template to remove.</param>
    /// <returns>True if successfully removed.</returns>
    public bool RemoveTemplate(GroupTemplate template)
    {
        // Find the template by FilePath since object references may differ after reload
        var templateToRemove = _templates.FirstOrDefault(t =>
            t == template ||
            (t.FilePath != null && t.FilePath == template.FilePath));

        if (templateToRemove == null)
            return false;

        // Delete file if it exists
        if (templateToRemove.FilePath != null && File.Exists(templateToRemove.FilePath))
        {
            try
            {
                File.Delete(templateToRemove.FilePath);
            }
            catch
            {
                // Ignore file deletion errors
            }
        }

        return _templates.Remove(templateToRemove);
    }

    private static int _instanceCounter = 1;

    /// <summary>
    /// Creates a deep copy of a group template for instantiation on the canvas.
    /// </summary>
    /// <param name="template">The template to instantiate.</param>
    /// <param name="x">X position for the new instance.</param>
    /// <param name="y">Y position for the new instance.</param>
    /// <returns>A new ComponentGroup instance with unique IDs.</returns>
    public ComponentGroup InstantiateTemplate(
        GroupTemplate template,
        double x,
        double y)
    {
        if (template.TemplateGroup == null)
            throw new InvalidOperationException("Template group is not loaded.");

        var deepCopy = template.TemplateGroup.DeepCopy();

        // Calculate the delta to move the group from its template position to the target position
        double deltaX = x - deepCopy.PhysicalX;
        double deltaY = y - deepCopy.PhysicalY;

        // Use MoveGroup to properly move the entire group (including children, pins, and paths)
        deepCopy.MoveGroup(deltaX, deltaY);

        // Use incremental counter instead of timestamp for cleaner names
        deepCopy.GroupName = $"{template.Name}_{_instanceCounter++}";

        // Rename child components with clean sequential names
        RenameComponentsWithSequentialNames(deepCopy);

        // Ensure S-Matrix is computed for all supported wavelengths
        deepCopy.EnsureSMatrixComputed();

        return deepCopy;
    }

    /// <summary>
    /// Sets human-readable display names on child components using sequential numbering.
    /// Component Identifiers remain as GUIDs for persistence stability.
    /// </summary>
    private void RenameComponentsWithSequentialNames(ComponentGroup group)
    {
        int componentIndex = 1;
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                childGroup.GroupName = $"SubGroup_{componentIndex++}";
                RenameComponentsWithSequentialNames(childGroup);
            }
            else
            {
                // Use existing HumanReadableName as base (preserves PDK names like "Grating Coupler TE 1550")
                // Fall back to NazcaFunctionName only if HumanReadableName is not set
                var baseName = !string.IsNullOrWhiteSpace(child.HumanReadableName)
                    ? StripNumberingSuffix(child.HumanReadableName)
                    : (!string.IsNullOrWhiteSpace(child.NazcaFunctionName)
                        ? child.NazcaFunctionName
                        : "Component");

                child.HumanReadableName = $"{baseName}_{componentIndex++}";
            }
        }
    }

    /// <summary>
    /// Strips the numbering suffix from a component name.
    /// E.g., "Grating Coupler TE 1550_1" -> "Grating Coupler TE 1550"
    /// </summary>
    private static string StripNumberingSuffix(string name)
    {
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var suffix = name.Substring(lastUnderscore + 1);
            // Only strip if suffix is a number
            if (int.TryParse(suffix, out _))
            {
                return name.Substring(0, lastUnderscore);
            }
        }
        return name;
    }

    /// <summary>
    /// Gets the default library path in the application data folder.
    /// </summary>
    private static string GetDefaultLibraryPath()
    {
        var appData = Helpers.AppDataFolders.LocalApplicationData;
        return Path.Combine(appData, "ConnectAPICPro", DefaultLibraryFolder);
    }

    /// <summary>
    /// Ensures library subdirectories exist.
    /// </summary>
    private void EnsureLibraryDirectoriesExist()
    {
        Directory.CreateDirectory(Path.Combine(_libraryBasePath, UserGroupsSubfolder));
        Directory.CreateDirectory(Path.Combine(_libraryBasePath, PdkGroupsSubfolder));
    }

    /// <summary>
    /// Loads templates from a specific folder.
    /// </summary>
    private void LoadTemplatesFromFolder(string folderPath, string source)
    {
        if (!Directory.Exists(folderPath))
            return;

        foreach (var filePath in Directory.GetFiles(folderPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var fileData = JsonSerializer.Deserialize<GroupLibraryFileData>(json);

                if (fileData == null)
                    continue;

                // Deserialize the full group data if available
                ComponentGroup? templateGroup = null;
                if (!string.IsNullOrWhiteSpace(fileData.GroupData))
                {
                    templateGroup = GroupTemplateSerializer.Deserialize(fileData.GroupData);
                }

                var template = new GroupTemplate
                {
                    Name = fileData.Name,
                    Description = fileData.Description,
                    Category = source == "User" ? "User Groups" : "PDK Macros",
                    FilePath = filePath,
                    ComponentCount = fileData.ComponentCount,
                    WidthMicrometers = fileData.WidthMicrometers,
                    HeightMicrometers = fileData.HeightMicrometers,
                    Source = source,
                    CreatedAt = fileData.CreatedAt,
                    PreviewThumbnailBase64 = fileData.PreviewThumbnailBase64,
                    TemplateGroup = templateGroup
                };

                _templates.Add(template);
            }
            catch
            {
                // Skip malformed files
            }
        }
    }

    /// <summary>
    /// Makes a filename-safe version of a string.
    /// </summary>
    private static string MakeSafeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// JSON file structure for saved group templates.
/// Stores metadata and the full serialized ComponentGroup data for reconstruction.
/// </summary>
public class GroupLibraryFileData
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "User";
    public DateTime CreatedAt { get; set; }
    public int ComponentCount { get; set; }
    public double WidthMicrometers { get; set; }
    public double HeightMicrometers { get; set; }
    public string? PreviewThumbnailBase64 { get; set; }

    /// <summary>
    /// Serialized ComponentGroup data (JSON string from GroupTemplateSerializer).
    /// Contains all child components, frozen paths, and external pins.
    /// </summary>
    public string? GroupData { get; set; }
}

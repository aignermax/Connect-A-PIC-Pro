using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// Persists user-authored components into a writable, per-process user PDK file under
/// the user's local app-data directory. Never touches the bundled foundry PDK JSONs
/// (those live under <c>CAP-DataAccess/PDKs</c> and are read-only at runtime).
/// One PDK file per fabrication process, because the S-matrix and layout are
/// process-specific (issue #570).
/// </summary>
public sealed class UserPdkStore
{
    private readonly string _root;
    private readonly PdkJsonSaver _saver;
    private readonly PdkLoader _loader;

    /// <summary>Creates a store rooted at an explicit directory (used by tests).</summary>
    public UserPdkStore(string userPdkRootDirectory, PdkJsonSaver saver, PdkLoader loader)
    {
        _root = userPdkRootDirectory;
        _saver = saver;
        _loader = loader;
    }

    /// <summary>
    /// Creates the store used at runtime, rooted at
    /// <c>%LOCALAPPDATA%/Lunima/user-pdks</c> (per-user, per-machine; never inside the
    /// installed application directory so it survives reinstalls/updates).
    /// </summary>
    public static UserPdkStore CreateDefault() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "user-pdks"),
        new PdkJsonSaver(),
        new PdkLoader());

    /// <summary>The user-PDK file path for a fabrication process. Does not create the file.</summary>
    public string ResolvePath(ProcessDefinition process) =>
        Path.Combine(_root, Slug(process.Name) + ".json");

    /// <summary>True when a component of that name is already stored for the process.</summary>
    public bool ComponentExists(ProcessDefinition process, string componentName)
    {
        var path = ResolvePath(process);
        if (!File.Exists(path))
        {
            return false;
        }

        var pdk = _loader.LoadFromFileForEditing(path);
        return pdk.Components.Exists(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds or replaces (by name, case-insensitive) the component in the process's user PDK,
    /// creating the PDK file on first use. Returns the file path written to.
    /// </summary>
    public string Save(ProcessDefinition process, PdkComponentDraft component, string backend, string? routingCrossSection)
    {
        var path = ResolvePath(process);
        Directory.CreateDirectory(_root);

        var pdk = File.Exists(path)
            ? _loader.LoadFromFileForEditing(path)
            : NewPdk(process, backend, routingCrossSection);

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, path);
        return path;
    }

    private static PdkDraft NewPdk(ProcessDefinition process, string backend, string? routingCrossSection) => new()
    {
        Name = $"My {process.Name} Components",
        Foundry = process.Foundry,
        Backend = backend,
        Process = process,
        GdsFactoryRoutingCrossSection = routingCrossSection,
        Components = new()
    };

    /// <summary>
    /// Converts a process display name into a filesystem- and culture-invariant slug
    /// (lowercase, non-alphanumeric runs collapsed to a single hyphen).
    /// </summary>
    private static string Slug(string name)
    {
        var lower = (name ?? string.Empty).ToLower(CultureInfo.InvariantCulture);
        var slug = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "custom" : slug;
    }
}

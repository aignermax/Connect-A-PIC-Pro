using System;
using System.Collections.Generic;
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
    /// The file path a named custom PDK would live at. Does not create the file.
    /// </summary>
    public string ResolveNamedPath(string pdkName) =>
        Path.Combine(_root, Slug(pdkName) + ".json");

    /// <summary>True when a named custom PDK file already exists for that name.</summary>
    public bool NamedPdkExists(string pdkName) => File.Exists(ResolveNamedPath(pdkName));

    /// <summary>
    /// Creates a new, empty named custom PDK file (no components yet) bound to a
    /// fabrication process, for the "PDK first, then add components" wizard flow.
    /// Callers must check <see cref="NamedPdkExists"/> first; this method refuses to
    /// silently overwrite an existing file with the same name.
    /// </summary>
    /// <exception cref="InvalidOperationException">A named PDK with that name already exists.</exception>
    public string CreateNamedPdkWithProcess(string pdkName, ProcessDefinition process, string backend, string? routingCrossSection)
    {
        if (NamedPdkExists(pdkName))
        {
            throw new InvalidOperationException($"A custom PDK named '{pdkName}' already exists.");
        }

        var path = ResolveNamedPath(pdkName);
        Directory.CreateDirectory(_root);

        var draft = new PdkDraft
        {
            Name = pdkName,
            Foundry = process.Foundry,
            Backend = backend,
            Process = process,
            GdsFactoryRoutingCrossSection = routingCrossSection,
            Components = new()
        };

        _saver.SaveToFile(draft, path);
        return path;
    }

    /// <summary>True when a component of that name exists in the PDK file at <paramref name="filePath"/>.</summary>
    public bool ComponentExistsInFile(string filePath, string componentName)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        var pdk = _loader.LoadFromFileForEditing(filePath);
        return pdk.Components.Exists(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists every named custom PDK found directly under the store root: every
    /// <c>*.json</c> file that loads successfully and declares a <see cref="ProcessDefinition"/>.
    /// Unreadable or process-less files (e.g. legacy per-process files predating #570, or
    /// files damaged outside the app) are silently skipped rather than failing the listing.
    /// </summary>
    public IReadOnlyList<UserPdkInfo> ListCustomPdks()
    {
        var result = new List<UserPdkInfo>();
        if (!Directory.Exists(_root))
        {
            return result;
        }

        foreach (var path in Directory.GetFiles(_root, "*.json"))
        {
            try
            {
                var pdk = _loader.LoadFromFileForEditing(path);
                if (pdk.Process is not null)
                {
                    result.Add(new UserPdkInfo(pdk.Name, path, pdk.Process));
                }
            }
            catch
            {
                // Skip files that don't parse as a valid PDK draft — the listing
                // must stay usable even if one custom PDK file is malformed.
            }
        }

        return result;
    }

    /// <summary>
    /// Adds or replaces (by name, case-insensitive) the component in a user-named custom
    /// PDK, independent of any single fabrication process's default file. Creates the file
    /// (named <c>&lt;slug(pdkName)&gt;.json</c>) on first use. Returns the file path written to.
    /// </summary>
    public string SaveToNamedPdk(string pdkName, ProcessDefinition process, PdkComponentDraft component, string backend, string? routingCrossSection)
    {
        var path = ResolveNamedPath(pdkName);
        Directory.CreateDirectory(_root);

        var pdk = File.Exists(path)
            ? _loader.LoadFromFileForEditing(path)
            : NewNamedPdk(pdkName, process, backend, routingCrossSection);
        pdk.Name = pdkName;
        pdk.Process = process;

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, path);
        return path;
    }

    /// <summary>
    /// Adds or replaces (by name, case-insensitive) the component in an already-created
    /// named custom PDK file. When <paramref name="replacesName"/> is given (an edit-mode
    /// rename), the component previously stored under that name is removed too, so the
    /// rename never leaves an orphaned original behind (issue #730).
    /// Returns <paramref name="filePath"/> for chaining.
    /// </summary>
    public string AppendToExistingPdk(string filePath, PdkComponentDraft component, string? replacesName = null)
    {
        var pdk = _loader.LoadFromFileForEditing(filePath);

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(replacesName))
        {
            pdk.Components.RemoveAll(c => string.Equals(c.Name, replacesName, StringComparison.OrdinalIgnoreCase));
        }
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, filePath);
        return filePath;
    }

    private static PdkDraft NewNamedPdk(string pdkName, ProcessDefinition process, string backend, string? routingCrossSection) => new()
    {
        Name = pdkName,
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

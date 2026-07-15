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
    /// The default root directory used by <see cref="CreateDefault"/> —
    /// <c>%LOCALAPPDATA%/Lunima/user-pdks</c> (per-user, per-machine; never inside the
    /// installed application directory so it survives reinstalls/updates). Exposed as a
    /// standalone path so callers that need to scan the directory without a full store
    /// instance (e.g. the startup PDK reload, issue #700) share this single source of truth
    /// instead of duplicating the path formula.
    /// </summary>
    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "user-pdks");

    /// <summary>
    /// Creates the store used at runtime, rooted at <see cref="DefaultRootDirectory"/>.
    /// </summary>
    public static UserPdkStore CreateDefault() => new(DefaultRootDirectory, new PdkJsonSaver(), new PdkLoader());

    /// <summary>
    /// Forks a bundled (read-only, shipped) PDK into the editable user-PDK store so the user can
    /// edit it without touching the installed app directory (which is overwritten on update).
    /// Copies the bundled JSON to <c>&lt;root&gt;/&lt;slug(name)&gt;.json</c> and returns that path. If a user
    /// PDK of the same name already exists (already forked, or a same-named user PDK), returns its
    /// existing path unchanged — forking is idempotent and never clobbers user edits.
    /// </summary>
    /// <param name="bundledFilePath">Full path of the bundled PDK JSON to fork.</param>
    /// <param name="pdkName">Display name of the bundled PDK (drives the user-copy file name).</param>
    /// <returns>The user-PDK file path holding the (now editable) fork.</returns>
    public string ForkBundledPdk(string bundledFilePath, string pdkName)
    {
        var target = ResolveNamedPath(pdkName);
        if (File.Exists(target))
            return target; // already forked / a same-named user PDK exists — don't overwrite

        Directory.CreateDirectory(_root);
        File.Copy(bundledFilePath, target);
        return target;
    }

    /// <summary>
    /// Creates a <see cref="PdkTrashService"/> over this store's SAME root, so restore reads the
    /// exact <c>.trash</c> folder that <see cref="MoveToTrash"/> / <see cref="RemoveComponent"/>
    /// write into (no risk of a default-root mismatch when the store was constructed elsewhere).
    /// </summary>
    public PdkTrashService CreateTrashService() => new(_root, _loader, _saver);

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
    /// named custom PDK file. Returns <paramref name="filePath"/> for chaining.
    /// </summary>
    public string AppendToExistingPdk(string filePath, PdkComponentDraft component)
    {
        var pdk = _loader.LoadFromFileForEditing(filePath);

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
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
    /// Moves a user PDK file to the store's <c>.trash</c> subfolder (issue #737/LC-T5), so a
    /// deleted custom PDK can still be restored by hand instead of being gone for good. The
    /// destination name is the original file's base name plus an invariant-culture
    /// <c>yyyyMMdd-HHmmss</c> timestamp (see <see cref="ResolveTrashDestination"/>) — repeated
    /// deletes of the same PDK never overwrite an earlier trashed copy. Callers are responsible
    /// for the bundled-PDK guard (this store has no notion of "bundled"; that classification
    /// lives in <c>PdkManagerViewModel</c>/<c>PdkInfoViewModel.IsBundled</c> at the UI layer) —
    /// this method moves whatever path it is given.
    /// </summary>
    /// <param name="filePath">Full path of the PDK file to trash.</param>
    /// <returns>The full path the file was moved to under <c>.trash</c>.</returns>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="filePath"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="filePath"/> is not under this store's managed root (see
    /// <see cref="IsInManagedRoot"/>) — an externally-stored file must never be relocated into
    /// the app-data trash.
    /// </exception>
    public string MoveToTrash(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"PDK file not found: {filePath}", filePath);
        }
        if (!IsInManagedRoot(filePath))
        {
            throw new InvalidOperationException(
                $"'{filePath}' is outside the managed user-PDK directory and must not be moved to its trash.");
        }

        var trashPath = ResolveTrashDestination(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
        File.Move(filePath, trashPath);
        return trashPath;
    }

    /// <summary>
    /// True when <paramref name="filePath"/> lies directly under this store's root directory.
    /// PDKs imported from arbitrary external folders (remembered via preferences) are registered
    /// in the library but their files are NOT managed by this store — deleting one from the
    /// library must leave the user's file untouched where they keep it, instead of relocating it
    /// into a hidden app-data trash folder (PR #739 review). Callers use this to decide between
    /// <see cref="MoveToTrash"/> (managed) and unregister-only (external).
    /// </summary>
    public bool IsInManagedRoot(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes a single component (matched by name, case-insensitive) from the PDK file at
    /// <paramref name="filePath"/>, rewriting the file without it. When
    /// <paramref name="backupFirst"/> is true (the default), the file's PRE-EDIT content is
    /// first copied into <c>.trash</c> (same naming as <see cref="MoveToTrash"/>) so an
    /// accidental component delete can be recovered by hand — unlike <see cref="MoveToTrash"/>,
    /// the PDK file itself is not moved; only one component leaves it.
    /// </summary>
    /// <param name="filePath">Full path of the PDK file to edit.</param>
    /// <param name="componentName">Name of the component to remove (case-insensitive).</param>
    /// <param name="backupFirst">When true, backs up the pre-edit file into <c>.trash</c> first.</param>
    /// <returns>
    /// <paramref name="filePath"/> on success, or <c>null</c> as a tolerated no-op when the file
    /// does not exist or no component with that name is present (mirrors the tolerant style of
    /// <see cref="ComponentExistsInFile"/> — a missing target is not an error here).
    /// </returns>
    public string? RemoveComponent(string filePath, string componentName, bool backupFirst = true)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var pdk = _loader.LoadFromFileForEditing(filePath);
        var removedCount = pdk.Components.RemoveAll(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));
        if (removedCount == 0)
        {
            return null;
        }

        if (backupFirst)
        {
            var trashPath = ResolveTrashDestination(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
            File.Copy(filePath, trashPath);
        }

        _saver.SaveToFile(pdk, filePath);
        return filePath;
    }

    /// <summary>
    /// Computes a unique destination path under <c>&lt;root&gt;/.trash</c> for
    /// <paramref name="filePath"/>: its base file name plus an invariant-culture
    /// <c>yyyyMMdd-HHmmss</c> timestamp, with a numeric suffix appended if that exact name is
    /// already taken (e.g. two trash operations on the same PDK within the same second).
    /// </summary>
    private string ResolveTrashDestination(string filePath)
    {
        var trashDir = Path.Combine(_root, TrashDirectoryName);
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var candidate = Path.Combine(trashDir, $"{baseName}-{timestamp}.json");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(trashDir, $"{baseName}-{timestamp}-{suffix}.json");
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Name of the trash subfolder under the store root. Excluded from the startup directory
    /// scan (<c>Directory.GetFiles(root, "*.json")</c> defaults to <c>TopDirectoryOnly</c>, see
    /// <c>LeftPanelViewModel.CollectUserPdkCandidatePaths</c>) without needing a special case.
    /// </summary>
    private const string TrashDirectoryName = ".trash";

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

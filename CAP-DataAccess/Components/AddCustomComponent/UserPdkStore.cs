using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

public sealed class UserPdkStore
{
    private readonly string _root;
    private readonly PdkJsonSaver _saver;
    private readonly PdkLoader _loader;

    public UserPdkStore(string userPdkRootDirectory, PdkJsonSaver saver, PdkLoader loader)
    {
        _root = userPdkRootDirectory;
        _saver = saver;
        _loader = loader;
    }

    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "user-pdks");

    public static UserPdkStore CreateDefault() => new(DefaultRootDirectory, new PdkJsonSaver(), new PdkLoader());

    /// <summary>The writable root directory every managed user-PDK file (and its sidecar files, e.g. imported .gds) lives in.</summary>
    public string RootDirectory => _root;

    public string ForkBundledPdk(string bundledFilePath, string pdkName)
    {
        var target = ResolveNamedPath(pdkName);
        if (File.Exists(target))
            return target;

        Directory.CreateDirectory(_root);
        File.Copy(bundledFilePath, target);
        return target;
    }

    /// <summary>
    /// Writes an edited whole-PDK draft as the user's fork of a bundled PDK
    /// (offset-editor save path). The bundled JSON is never touched; the draft
    /// is saved to the fork location in the managed root. If a fork already
    /// exists (e.g. created earlier by the component editor), its previous
    /// state is backed up to <c>.trash</c> before being replaced, so no user
    /// edit is silently lost. Returns the fork file path.
    /// </summary>
    public string SaveDraftAsFork(PdkDraft draft, string pdkName)
    {
        var target = ResolveNamedPath(pdkName);
        Directory.CreateDirectory(_root);

        if (File.Exists(target))
        {
            var trashPath = ResolveTrashDestination(target);
            Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
            File.Copy(target, trashPath);
        }

        _saver.SaveToFile(draft, target);
        return target;
    }

    public PdkTrashService CreateTrashService() => new(_root, _loader, _saver);

    public string ResolvePath(ProcessDefinition process) =>
        Path.Combine(_root, Slug(process.Name) + ".json");

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

    public string ResolveNamedPath(string pdkName) =>
        Path.Combine(_root, Slug(pdkName) + ".json");

    public bool NamedPdkExists(string pdkName) => File.Exists(ResolveNamedPath(pdkName));

    /// <summary>
    /// Resolves <paramref name="desiredName"/> to a PDK name whose slug-target file is
    /// safe to write: the name itself when no such file exists or the existing file
    /// holds the SAME PDK (a re-import merges into it), or a deterministic
    /// <c>-2</c>, <c>-3</c>, … suffix when the slug collides with a DIFFERENT PDK —
    /// e.g. "my circuit" and "my-circuit" both slug to the same file name, and the
    /// second import must never merge into (or overwrite) the first one's file.
    /// </summary>
    public string ResolveAvailablePdkName(string desiredName)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? desiredName : $"{desiredName}-{n}";
            var path = ResolveNamedPath(candidate);
            if (!File.Exists(path))
            {
                return candidate;
            }
            if (string.Equals(LoadExistingForEditing(path).Name, candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
    }

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
    /// Lists every user-managed PDK in the root directory: process-bound ones and
    /// process-agnostic ones (e.g. created by a GDS import). Membership in the root
    /// directory IS the "user-defined" classification — bundled PDKs never live here —
    /// so a process-agnostic file must not be excluded merely for declaring no
    /// fabrication process (its <see cref="UserPdkInfo.Process"/> is null then).
    /// Unreadable files and files with neither a process nor the process-agnostic
    /// flag are skipped.
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
                if (pdk.Process is not null || pdk.ProcessAgnostic)
                {
                    result.Add(new UserPdkInfo(pdk.Name, path, pdk.Process));
                }
            }
            catch
            {
            }
        }

        return result;
    }

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

    public string AppendToExistingPdk(string filePath, PdkComponentDraft component)
    {
        var pdk = _loader.LoadFromFileForEditing(filePath);

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, filePath);
        return filePath;
    }

    /// <summary>
    /// Adds or replaces <paramref name="component"/> in a named, process-agnostic user PDK
    /// (created on first use, one load-modify-save per call). Process-agnostic PDKs declare
    /// no fabrication process, so the library keeps their components placeable under every
    /// active process — the right shape for geometry-only imports (e.g. GDS) that no
    /// foundry PDK claims. Returns the PDK file path.
    /// </summary>
    public string SaveToProcessAgnosticNamedPdk(string pdkName, PdkComponentDraft component, string backend)
    {
        var path = ResolveNamedPath(pdkName);
        Directory.CreateDirectory(_root);

        var pdk = File.Exists(path)
            ? LoadExistingForEditing(path)
            : new PdkDraft { Name = pdkName, Backend = backend, Components = new() };
        pdk.Name = pdkName;
        pdk.ProcessAgnostic = true;

        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, path);
        return path;
    }

    /// <summary>
    /// Loads an existing managed PDK file for a load-modify-save cycle. A corrupt
    /// or hand-edited file aborts the operation with a user-presentable
    /// <see cref="InvalidDataException"/> naming the broken file instead of
    /// surfacing a raw validation/deserialization error mid-import.
    /// </summary>
    private PdkDraft LoadExistingForEditing(string path)
    {
        try
        {
            return _loader.LoadFromFileForEditing(path);
        }
        catch (Exception ex) when (ex is PdkValidationException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"The existing user PDK file '{path}' could not be read (corrupted or hand-edited?): " +
                $"{ex.Message} Fix or remove the file and try again — the import was aborted.", ex);
        }
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

    public bool IsInManagedRoot(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var root = Path.GetFullPath(_root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces (or adds) <paramref name="component"/> in the PDK file with a single
    /// load-modify-save — any failure leaves the file fully old or fully new, never with the
    /// component missing. Optionally backs the previous state up to <c>.trash</c> first.
    /// Returns false when the file does not exist.
    /// </summary>
    public bool ReplaceComponent(string filePath, PdkComponentDraft component, bool backupFirst = true)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        var pdk = _loader.LoadFromFileForEditing(filePath);
        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        if (backupFirst)
        {
            var trashPath = ResolveTrashDestination(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
            File.Copy(filePath, trashPath);
        }

        _saver.SaveToFile(pdk, filePath);
        return true;
    }

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

    private const string TrashDirectoryName = ".trash";

    private static string Slug(string name)
    {
        var lower = (name ?? string.Empty).ToLower(CultureInfo.InvariantCulture);
        var slug = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "custom" : slug;
    }
}

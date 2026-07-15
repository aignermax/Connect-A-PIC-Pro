using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// Reads and restores items from the user-PDK <c>.trash</c> folder that
/// <see cref="UserPdkStore.MoveToTrash"/> (deleted PDK) and
/// <see cref="UserPdkStore.RemoveComponent"/> (pre-edit component backup) write into.
///
/// Both operations leave a full PDK JSON named <c>&lt;base&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>, so a
/// trash file's kind is inferred, not stored: if the original live file
/// (<c>&lt;root&gt;/&lt;base&gt;.json</c>) is gone it was a deleted PDK; if it still exists the backup is
/// a removed-components snapshot, and the restorable set is the components present in the backup
/// but missing from the live file. This keeps restore purely additive — it never clobbers newer
/// edits — and needs no extra metadata on disk.
/// </summary>
public sealed class PdkTrashService
{
    private const string TrashDirectoryName = ".trash";
    private static readonly Regex TimestampSuffix =
        new(@"^(?<base>.+)-(?<date>\d{8})-(?<time>\d{6})(?:-\d+)?$", RegexOptions.Compiled);

    private readonly string _root;
    private readonly PdkLoader _loader;
    private readonly PdkJsonSaver _saver;

    /// <summary>Creates a trash service over an explicit user-PDK root (tests).</summary>
    public PdkTrashService(string userPdkRootDirectory, PdkLoader loader, PdkJsonSaver saver)
    {
        _root = userPdkRootDirectory;
        _loader = loader;
        _saver = saver;
    }

    /// <summary>Creates the runtime service rooted at <see cref="UserPdkStore.DefaultRootDirectory"/>.</summary>
    public static PdkTrashService CreateDefault() =>
        new(UserPdkStore.DefaultRootDirectory, new PdkLoader(), new PdkJsonSaver());

    /// <summary>Absolute path of the trash folder (may not exist yet).</summary>
    public string TrashDirectory => Path.Combine(_root, TrashDirectoryName);

    /// <summary>
    /// Lists recoverable trash entries, newest first. Unparseable/unreadable files and
    /// removed-components backups whose components are all already back in the live file are
    /// skipped, so the list only ever shows items that can actually be restored.
    /// </summary>
    public IReadOnlyList<PdkTrashEntry> ListEntries()
    {
        var entries = new List<PdkTrashEntry>();
        if (!Directory.Exists(TrashDirectory))
            return entries;

        foreach (var path in Directory.GetFiles(TrashDirectory, "*.json"))
        {
            var entry = TryReadEntry(path);
            if (entry != null && (entry.Kind == PdkTrashKind.DeletedPdk || entry.RestorableComponentNames.Count > 0))
                entries.Add(entry);
        }

        return entries.OrderByDescending(e => e.DeletedAt).ToList();
    }

    private PdkTrashEntry? TryReadEntry(string trashPath)
    {
        PdkDraft backup;
        try { backup = _loader.LoadFromFileForEditing(trashPath); }
        catch { return null; } // a damaged trash file must not break the whole listing

        var fileName = Path.GetFileNameWithoutExtension(trashPath);
        var match = TimestampSuffix.Match(fileName);
        if (!match.Success)
            return null;

        var deletedAt = ParseTimestamp(match.Groups["date"].Value, match.Groups["time"].Value);
        var livePath = Path.Combine(_root, match.Groups["base"].Value + ".json");
        var backupComponents = backup.Components.Select(c => c.Name).ToList();

        if (!File.Exists(livePath))
            return new PdkTrashEntry(trashPath, backup.Name, PdkTrashKind.DeletedPdk, deletedAt, livePath, backupComponents);

        // Live file still exists → this is a component backup. Restorable = in backup, not in live.
        var liveNames = TryLoadComponentNames(livePath);
        var restorable = backup.Components
            .Where(c => !liveNames.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
        return new PdkTrashEntry(trashPath, backup.Name, PdkTrashKind.RemovedComponents, deletedAt, livePath, restorable);
    }

    private HashSet<string> TryLoadComponentNames(string livePath)
    {
        try
        {
            return _loader.LoadFromFileForEditing(livePath).Components
                .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Live file unreadable → treat everything in the backup as restorable.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Restores <paramref name="entry"/>. A deleted PDK's file is moved back under the store root
    /// (with a numeric suffix if a PDK of that name was created since); a removed-components backup
    /// re-adds only the still-missing components to the live file. Returns what was restored so the
    /// caller can re-register it into the library.
    /// </summary>
    public PdkTrashRestoreResult Restore(PdkTrashEntry entry)
    {
        return entry.Kind == PdkTrashKind.DeletedPdk
            ? RestoreDeletedPdk(entry)
            : RestoreRemovedComponents(entry);
    }

    private PdkTrashRestoreResult RestoreDeletedPdk(PdkTrashEntry entry)
    {
        Directory.CreateDirectory(_root);
        var target = UniquePath(entry.OriginalLivePath);
        File.Move(entry.TrashFilePath, target);

        var restored = _loader.LoadFromFileForEditing(target);
        return new PdkTrashRestoreResult(target, restored.Name, PdkTrashKind.DeletedPdk,
            restored.Components.ToList());
    }

    private PdkTrashRestoreResult RestoreRemovedComponents(PdkTrashEntry entry)
    {
        var backup = _loader.LoadFromFileForEditing(entry.TrashFilePath);
        var live = _loader.LoadFromFileForEditing(entry.OriginalLivePath);
        var liveNames = live.Components.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var readded = backup.Components.Where(c => !liveNames.Contains(c.Name)).ToList();
        live.Components.AddRange(readded);
        _saver.SaveToFile(live, entry.OriginalLivePath);

        return new PdkTrashRestoreResult(entry.OriginalLivePath, live.Name, PdkTrashKind.RemovedComponents, readded);
    }

    /// <summary>Permanently deletes a trash file (irreversible). No-op if already gone.</summary>
    public void Purge(PdkTrashEntry entry)
    {
        if (File.Exists(entry.TrashFilePath))
            File.Delete(entry.TrashFilePath);
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired))
            return desired;

        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-restored-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static DateTime ParseTimestamp(string date, string time)
    {
        return DateTime.TryParseExact(date + time, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : DateTime.MinValue;
    }
}

/// <summary>Result of <see cref="PdkTrashService.Restore"/>: where the PDK now lives and what came back.</summary>
/// <param name="RestoredPdkPath">Live file path the restored PDK/components are in.</param>
/// <param name="PdkName">Display name of the restored PDK.</param>
/// <param name="Kind">Which restore path ran.</param>
/// <param name="RestoredComponents">Components actually added back (drafts, for re-registration).</param>
public sealed record PdkTrashRestoreResult(
    string RestoredPdkPath,
    string PdkName,
    PdkTrashKind Kind,
    IReadOnlyList<PdkComponentDraft> RestoredComponents);

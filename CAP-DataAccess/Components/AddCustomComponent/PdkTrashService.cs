using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

public sealed class PdkTrashService
{
    private const string TrashDirectoryName = ".trash";

    public const int RetentionDays = 30;

    private static readonly Regex TimestampSuffix =
        new(@"^(?<base>.+)-(?<date>\d{8})-(?<time>\d{6})(?:-(?<counter>\d+))?$", RegexOptions.Compiled);

    // Case-insensitive only on Windows: on case-sensitive file systems two paths differing in
    // case are genuinely different files and must not share a dedup bucket.
    private static readonly StringComparer LivePathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _root;
    private readonly PdkLoader _loader;
    private readonly PdkJsonSaver _saver;

    public PdkTrashService(string userPdkRootDirectory, PdkLoader loader, PdkJsonSaver saver)
    {
        _root = userPdkRootDirectory;
        _loader = loader;
        _saver = saver;
    }

    public static PdkTrashService CreateDefault() =>
        new(UserPdkStore.DefaultRootDirectory, new PdkLoader(), new PdkJsonSaver());

    public string TrashDirectory => Path.Combine(_root, TrashDirectoryName);

    public IReadOnlyList<PdkTrashEntry> ListEntries()
    {
        PurgeExpired();

        var entries = new List<PdkTrashEntry>();
        if (!Directory.Exists(TrashDirectory))
            return entries;

        // Each component-delete backs up the FULL pre-delete PDK file, so sequential deletes
        // leave overlapping backups. Emit one entry per missing component, keeping only the
        // newest backup per (livePath, componentName) — the one whose delete actually removed
        // it — so Restore brings back exactly the clicked component without resurrecting later
        // deletes. "Newest" ranks by the timestamp in the file NAME (counter as tiebreaker),
        // never mtime, which a git checkout/sync rewrites.
        var liveCache = new Dictionary<string, PdkDraft?>(LivePathComparer);
        var newestPerComponent = new Dictionary<(string LivePath, string Name), ((DateTime DeletedAt, int Counter) Rank, PdkTrashEntry Entry)>();

        foreach (var path in Directory.GetFiles(TrashDirectory, "*.json"))
        {
            var raw = TryReadRawInfo(path, liveCache);
            if (raw is null)
                continue;

            if (raw.Kind == PdkTrashKind.DeletedPdk)
            {
                if (raw.ComponentNames.Count > 0)
                    entries.Add(new PdkTrashEntry(path, raw.PdkName, PdkTrashKind.DeletedPdk, raw.DeletedAt, raw.LivePath, raw.ComponentNames));
                continue;
            }

            var rank = (raw.DeletedAt, raw.CollisionCounter);
            foreach (var name in raw.ComponentNames)
            {
                var key = (PathKey(raw.LivePath), name.ToLowerInvariant());
                if (newestPerComponent.TryGetValue(key, out var existing) && existing.Rank.CompareTo(rank) >= 0)
                    continue;

                newestPerComponent[key] = (rank,
                    new PdkTrashEntry(path, raw.PdkName, PdkTrashKind.RemovedComponents, raw.DeletedAt, raw.LivePath, new[] { name }));
            }
        }

        entries.AddRange(newestPerComponent.Values.Select(v => v.Entry));
        return entries.OrderByDescending(e => e.DeletedAt).ToList();
    }

    // Folds path case on Windows only — see LivePathComparer.
    private static string PathKey(string path) =>
        OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;

    private sealed record RawTrashInfo(
        string PdkName, PdkTrashKind Kind, DateTime DeletedAt, int CollisionCounter,
        string LivePath, IReadOnlyList<string> ComponentNames);

    private RawTrashInfo? TryReadRawInfo(string trashPath, Dictionary<string, PdkDraft?> liveCache)
    {
        PdkDraft backup;
        try { backup = _loader.LoadFromFileForEditing(trashPath); }
        catch { return null; }

        var fileName = Path.GetFileNameWithoutExtension(trashPath);
        var match = TimestampSuffix.Match(fileName);
        if (!match.Success)
            return null;

        var deletedAt = ParseTimestamp(match.Groups["date"].Value, match.Groups["time"].Value);
        var counter = match.Groups["counter"].Success
            && int.TryParse(match.Groups["counter"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : 0;
        var livePath = Path.Combine(_root, match.Groups["base"].Value + ".json");
        var backupComponents = backup.Components.Select(c => c.Name).ToList();

        // Several backups point at the same live PDK file — parse it once per ListEntries call.
        if (!liveCache.TryGetValue(livePath, out var live))
        {
            live = TryLoadLive(livePath);
            liveCache[livePath] = live;
        }

        bool sameLivePdk = live != null && string.Equals(live.Name, backup.Name, StringComparison.OrdinalIgnoreCase);
        if (!sameLivePdk)
            return new RawTrashInfo(backup.Name, PdkTrashKind.DeletedPdk, deletedAt, counter, livePath, backupComponents);

        var liveNames = live!.Components.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restorable = backup.Components
            .Where(c => !liveNames.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
        return new RawTrashInfo(backup.Name, PdkTrashKind.RemovedComponents, deletedAt, counter, livePath, restorable);
    }

    private PdkDraft? TryLoadLive(string livePath)
    {
        if (!File.Exists(livePath))
            return null;
        try { return _loader.LoadFromFileForEditing(livePath); }
        catch { return null; }
    }

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
        if (!string.Equals(target, entry.OriginalLivePath, StringComparison.OrdinalIgnoreCase))
        {
            restored.Name = $"{restored.Name} (restored)";
            _saver.SaveToFile(restored, target);
        }

        return new PdkTrashRestoreResult(target, restored.Name, PdkTrashKind.DeletedPdk,
            restored.Components.ToList());
    }

    private PdkTrashRestoreResult RestoreRemovedComponents(PdkTrashEntry entry)
    {
        var backup = _loader.LoadFromFileForEditing(entry.TrashFilePath);
        var live = _loader.LoadFromFileForEditing(entry.OriginalLivePath);
        var liveNames = live.Components.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = entry.RestorableComponentNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Restore ONLY the component(s) this entry names: the backup is a full pre-delete
        // snapshot and may still contain components a later delete removed — those must stay gone.
        var readded = backup.Components
            .Where(c => wanted.Contains(c.Name) && !liveNames.Contains(c.Name))
            .ToList();
        live.Components.AddRange(readded);
        _saver.SaveToFile(live, entry.OriginalLivePath);

        return new PdkTrashRestoreResult(entry.OriginalLivePath, live.Name, PdkTrashKind.RemovedComponents, readded);
    }

    public void PurgeExpired() => PurgeExpired(DateTime.Now);

    internal void PurgeExpired(DateTime now)
    {
        if (!Directory.Exists(TrashDirectory))
            return;

        var cutoff = now.AddDays(-RetentionDays);
        foreach (var path in Directory.GetFiles(TrashDirectory, "*.json"))
        {
            var match = TimestampSuffix.Match(Path.GetFileNameWithoutExtension(path));
            if (!match.Success)
                continue;

            var deletedAt = ParseTimestamp(match.Groups["date"].Value, match.Groups["time"].Value);
            if (deletedAt != DateTime.MinValue && deletedAt < cutoff)
                TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
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

public sealed record PdkTrashRestoreResult(
    string RestoredPdkPath,
    string PdkName,
    PdkTrashKind Kind,
    IReadOnlyList<PdkComponentDraft> RestoredComponents);

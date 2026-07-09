// CAP.Avalonia/Controls/Canvas/ComponentPreview/GdsPreviewDiskCache.cs
using CAP_Core.Export;

namespace CAP.Avalonia.Controls.Canvas.ComponentPreview;

/// <summary>
/// Persistent, cross-platform on-disk cache for GDS preview geometry.
/// One JSON file per <see cref="GdsPreviewKey"/> hash. A present-but-empty marker
/// records "render produced nothing" so no retry happens across sessions.
/// All I/O is best-effort: any failure is treated as a miss and never throws.
/// </summary>
public sealed class GdsPreviewDiskCache
{
    private const string EmptyMarker = "__EMPTY__";
    private readonly string _directory;

    /// <summary>Production ctor: uses the platform-neutral local app-data location.</summary>
    public GdsPreviewDiskCache()
        : this(Path.Combine(
            CAP_Core.Helpers.AppDataFolders.LocalApplicationData,
            "Lunima", "gds-preview-cache"))
    {
    }

    /// <summary>Testable ctor: cache files live under <paramref name="directory"/>.</summary>
    public GdsPreviewDiskCache(string directory) => _directory = directory;

    private string PathFor(GdsPreviewKey key) => Path.Combine(_directory, key.Hash() + ".json");

    /// <summary>
    /// Reads a cached entry. Returns true when the key is present on disk (even when
    /// the stored value is the empty marker, in which case <paramref name="result"/>
    /// is null). Returns false on miss or any read/parse error (corrupt file).
    /// </summary>
    public bool TryRead(GdsPreviewKey key, out NazcaPreviewResult? result)
    {
        result = null;
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (json == EmptyMarker) return true;   // present, empty
            result = NazcaPreviewResultDto.Deserialize(json);
            return result != null;                   // corrupt parse → miss
        }
        catch { return false; }
    }

    /// <summary>Persists a successful render result.</summary>
    public void Write(GdsPreviewKey key, NazcaPreviewResult result)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(key), NazcaPreviewResultDto.Serialize(result));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Records that a render produced nothing (no retry next session).</summary>
    public void WriteEmpty(GdsPreviewKey key)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(key), EmptyMarker);
        }
        catch { /* best-effort */ }
    }
}

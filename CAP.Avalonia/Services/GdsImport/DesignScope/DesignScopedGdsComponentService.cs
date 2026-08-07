using System.Security.Cryptography;
using System.Text.Json;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.GdsImport.DesignScope;

/// <summary>
/// Holds the GDS-imported component sets of the CURRENTLY OPEN design (issue
/// #830). Imported components live in the .lun file — not in a global user
/// PDK — so they exist only while their design is open and never leak into
/// other designs. The service owns the design-scope lifecycle: add on import,
/// capture on save, restore on load, clear on new/other design.
/// <para>
/// Runtime registration/removal in the component library goes through the two
/// constructor callbacks (wired to <c>LeftPanelViewModel</c> in DI); null
/// callbacks skip library registration (headless tests). The embedded .gds
/// bytes are materialized into a content-addressed cache file so the drafts'
/// raw code (Python <c>nd.load_gds</c>) has a real absolute path at runtime.
/// UI-thread only, like library registration itself.
/// </para>
/// </summary>
public sealed partial class DesignScopedGdsComponentService
{
    private readonly List<DesignScopedGdsSet> _sets = new();
    private readonly Action<string, IReadOnlyList<PdkComponentDraft>>? _registerPdk;
    private readonly Action<string>? _removePdk;
    private readonly string _gdsCacheDirectory;
    private readonly UserPdkStore _userPdkStore;
    private readonly PdkLoader _pdkLoader;

    /// <summary>Hex characters of the content hash used as the cache file stem (collision-safe at this scale).</summary>
    private const int CacheFileHashLength = 16;

    /// <summary>Initializes the service.</summary>
    /// <param name="registerPdk">
    /// Registers a design-scoped PDK's components in the runtime library:
    /// (pdkName, drafts with the absolute cache path already substituted into
    /// the raw code). Null skips library registration.
    /// </param>
    /// <param name="removePdk">Removes a design-scoped PDK from the runtime library by name. Null skips removal.</param>
    /// <param name="gdsCacheDirectory">Cache directory for materialized .gds files; defaults to <see cref="DefaultGdsCacheDirectory"/>.</param>
    /// <param name="userPdkStore">Legacy-migration source store; defaults to the managed user-PDK root.</param>
    /// <param name="pdkLoader">Loader for legacy import-PDK files; defaults to a fresh loader.</param>
    public DesignScopedGdsComponentService(
        Action<string, IReadOnlyList<PdkComponentDraft>>? registerPdk = null,
        Action<string>? removePdk = null,
        string? gdsCacheDirectory = null,
        UserPdkStore? userPdkStore = null,
        PdkLoader? pdkLoader = null)
    {
        _registerPdk = registerPdk;
        _removePdk = removePdk;
        _gdsCacheDirectory = gdsCacheDirectory ?? DefaultGdsCacheDirectory;
        _userPdkStore = userPdkStore ?? UserPdkStore.CreateDefault();
        _pdkLoader = pdkLoader ?? new PdkLoader();
    }

    /// <summary>Per-user cache for materialized .gds copies (content-addressed, safe to delete anytime).</summary>
    public static string DefaultGdsCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "gds-cache");

    /// <summary>The open design's imported component sets, in import order.</summary>
    public IReadOnlyList<DesignScopedGdsSet> Sets => _sets;

    /// <summary>
    /// Resolves <paramref name="desiredName"/> against the open design's set
    /// names: the name itself when free, otherwise a deterministic <c>-2</c>,
    /// <c>-3</c>, … suffix — importing the same file twice into one design must
    /// not merge or overwrite the first import's components.
    /// </summary>
    public string ResolveAvailablePdkName(string desiredName)
    {
        var candidate = desiredName;
        for (var n = 2; _sets.Any(s => s.PdkName.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{desiredName}-{n}";
        return candidate;
    }

    /// <summary>
    /// Writes <paramref name="gdsBytes"/> to the content-addressed cache
    /// (first <see cref="CacheFileHashLength"/> hex chars of the SHA-256 as the
    /// file stem) and returns the absolute path. Idempotent: identical content
    /// reuses the existing file, so re-opening a design writes nothing.
    /// </summary>
    public string MaterializeGds(byte[] gdsBytes)
    {
        Directory.CreateDirectory(_gdsCacheDirectory);
        var stem = Convert.ToHexString(SHA256.HashData(gdsBytes))[..CacheFileHashLength].ToLowerInvariant();
        var path = Path.Combine(_gdsCacheDirectory, stem + ".gds");
        if (!File.Exists(path))
            File.WriteAllBytes(path, gdsBytes);
        return path;
    }

    /// <summary>
    /// Adds an imported set to the open design's scope and registers its
    /// components in the runtime library. The stored drafts keep their portable
    /// token-form raw code; the registration copies get the materialized cache
    /// path substituted in.
    /// </summary>
    public void AddAndRegister(DesignScopedGdsSet set)
    {
        var cachePath = MaterializeGds(set.GdsBytes);
        _sets.Add(set);
        _registerPdk?.Invoke(set.PdkName, SubstituteForRuntime(set.Drafts, cachePath));
    }

    /// <summary>
    /// The .lun payload of the current design scope, or null when the design
    /// has no imported sets (so untouched designs serialize without the field).
    /// </summary>
    public List<ImportedGdsComponentSetData>? CaptureForSave()
    {
        if (_sets.Count == 0)
            return null;
        return _sets.Select(s => new ImportedGdsComponentSetData
        {
            PdkName = s.PdkName,
            GdsFileName = s.GdsFileName,
            GdsBase64 = Convert.ToBase64String(s.GdsBytes),
            Components = s.Drafts,
        }).ToList();
    }

    /// <summary>
    /// Removes every design-scoped PDK from the runtime library and forgets the
    /// sets — called when the design closes (new project, other design loaded)
    /// so imported components never leak into the next design.
    /// </summary>
    public void ClearDesignScope()
    {
        foreach (var set in _sets)
            _removePdk?.Invoke(set.PdkName);
        _sets.Clear();
    }

    /// <summary>
    /// Replaces the design scope with the sets stored in a loaded .lun. A set
    /// whose .gds payload cannot be decoded is skipped with a warning — the
    /// rest of the design still loads (its placements then report a missing
    /// template, matching the behavior for any unknown PDK).
    /// </summary>
    public void RestoreDesignScope(IEnumerable<ImportedGdsComponentSetData>? sets, Action<string>? warn = null)
    {
        ClearDesignScope();
        if (sets is null)
            return;

        foreach (var data in sets)
        {
            byte[] gdsBytes;
            try
            {
                gdsBytes = Convert.FromBase64String(data.GdsBase64);
            }
            catch (FormatException ex)
            {
                warn?.Invoke($"Imported GDS set '{data.PdkName}' could not be restored " +
                             $"(corrupt embedded GDS data): {ex.Message}");
                continue;
            }
            AddAndRegister(new DesignScopedGdsSet
            {
                PdkName = data.PdkName,
                GdsFileName = data.GdsFileName,
                GdsBytes = gdsBytes,
                Drafts = data.Components,
            });
        }
    }

    /// <summary>
    /// Deep-clones the drafts (JSON round-trip — registration copies must never
    /// alias the stored token-form drafts) and substitutes the cache path into
    /// their raw code.
    /// </summary>
    private static List<PdkComponentDraft> SubstituteForRuntime(
        IReadOnlyList<PdkComponentDraft> drafts, string gdsCachePath)
    {
        var clones = new List<PdkComponentDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            var clone = JsonSerializer.Deserialize<PdkComponentDraft>(JsonSerializer.Serialize(draft))!;
            if (clone.RawCode is not null)
                clone.RawCode = GdsCellDraftMapper.SubstituteGdsFileName(clone.RawCode, gdsCachePath);
            clones.Add(clone);
        }
        return clones;
    }
}

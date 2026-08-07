using System.Text.RegularExpressions;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport.DesignScope;

/// <summary>
/// Legacy migration (issue #830): designs saved before design-scoping reference
/// global <c>gds-import-*</c> user PDKs by <c>PdkSource</c> only. On first open
/// of such a design, the referenced legacy PDK files are converted into
/// design-scoped sets (drafts back to token form, .gds bytes embedded), so the
/// design becomes self-contained on its next save. The legacy files are NOT
/// deleted — another old design may still reference them; startup simply stops
/// loading them (see <c>LeftPanelViewModel.CollectUserPdkCandidatePaths</c>).
/// </summary>
public sealed partial class DesignScopedGdsComponentService
{
    /// <summary>Matches the double-quoted filename argument of the raw code's <c>nd.load_gds(...)</c> call.</summary>
    private static readonly Regex LoadGdsFileNameRegex = new(
        "load_gds\\(\\s*filename\\s*=\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled);

    /// <summary>
    /// Migrates every legacy global import PDK a loading design references into
    /// the design scope. Unmigratable sources (file/PDK/.gds missing or
    /// unreadable) are skipped with a warning and their placements resolve like
    /// any other missing template. Returns the number of migrated sets — the
    /// caller marks the design dirty when &gt; 0, so the next save embeds them.
    /// </summary>
    /// <param name="referencedPdkSources">All <c>PdkSource</c> values of the loading design's placements.</param>
    /// <param name="warn">Optional warning sink for skipped sources.</param>
    public int MigrateLegacyImportPdks(IEnumerable<string> referencedPdkSources, Action<string>? warn = null)
    {
        var migrated = 0;
        var legacySources = referencedPdkSources
            .Where(s => s.StartsWith(GdsImportService.ImportPdkNamePrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !_sets.Any(set => set.PdkName.Equals(s, StringComparison.OrdinalIgnoreCase)));

        foreach (var source in legacySources)
        {
            if (TryMigrateLegacyImportPdk(source, warn))
                migrated++;
        }
        return migrated;
    }

    private bool TryMigrateLegacyImportPdk(string pdkName, Action<string>? warn)
    {
        var pdkPath = _userPdkStore.ResolveNamedPath(pdkName);
        if (!File.Exists(pdkPath))
        {
            warn?.Invoke($"Design references imported PDK '{pdkName}', but no legacy PDK file " +
                         $"exists at '{pdkPath}' — its components are unavailable.");
            return false;
        }

        CAP_DataAccess.Components.ComponentDraftMapper.DTOs.PdkDraft legacy;
        try
        {
            legacy = _pdkLoader.LoadFromFileForEditing(pdkPath);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Legacy imported PDK '{pdkName}' could not be read for migration: {ex.Message}");
            return false;
        }

        if (!TryReadLegacyGdsSidecar(legacy.Components.Select(c => c.RawCode), pdkName, warn,
                out var escapedGdsPath, out var gdsPath, out var gdsBytes))
            return false;

        // Back to portable token form: the legacy files carry the absolute
        // sidecar path baked into the Python literal.
        foreach (var component in legacy.Components)
            component.RawCode = component.RawCode?.Replace(escapedGdsPath, GdsHierarchyImporter.GdsFileNameToken);

        AddAndRegister(new DesignScopedGdsSet
        {
            PdkName = pdkName,
            GdsFileName = Path.GetFileName(gdsPath),
            GdsBytes = gdsBytes,
            Drafts = legacy.Components,
        });
        return true;
    }

    /// <summary>
    /// Finds the .gds sidecar path in the legacy components' raw code (first
    /// <c>load_gds</c> filename argument) and reads its bytes. Conservative:
    /// any missing piece fails the whole set's migration rather than embedding
    /// a partial import.
    /// </summary>
    private static bool TryReadLegacyGdsSidecar(
        IEnumerable<string?> rawCodes, string pdkName, Action<string>? warn,
        out string escapedGdsPath, out string gdsPath, out byte[] gdsBytes)
    {
        escapedGdsPath = gdsPath = string.Empty;
        gdsBytes = Array.Empty<byte>();

        var match = rawCodes
            .Where(code => code is not null)
            .Select(code => LoadGdsFileNameRegex.Match(code!))
            .FirstOrDefault(m => m.Success);
        if (match is null)
        {
            warn?.Invoke($"Legacy imported PDK '{pdkName}' has no load_gds raw code — cannot migrate.");
            return false;
        }

        escapedGdsPath = match.Groups[1].Value;
        gdsPath = UnescapePythonStringLiteral(escapedGdsPath);
        try
        {
            gdsBytes = File.ReadAllBytes(gdsPath);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Legacy imported PDK '{pdkName}' references GDS file '{gdsPath}', " +
                         $"which could not be read: {ex.Message} — its components are unavailable.");
            return false;
        }
        return true;
    }

    /// <summary>Inverse of the import's Python-literal escaping (<c>\\</c> and <c>\"</c>).</summary>
    private static string UnescapePythonStringLiteral(string escaped) =>
        escaped.Replace("\\\"", "\"").Replace("\\\\", "\\");
}

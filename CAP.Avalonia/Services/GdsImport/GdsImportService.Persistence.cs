using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// The persistence helpers of <see cref="GdsImportService"/> — copying the
/// source .gds into the user-PDK root and the draft filtering/naming rules —
/// split out to keep the orchestration file under the project's 500-line gate.
/// </summary>
public sealed partial class GdsImportService
{
    /// <summary>
    /// Copies the source .gds into the user-PDK root, content-aware: an existing
    /// file with identical content is reused, a same-named file with DIFFERENT
    /// content is never overwritten — the copy gets a <c>-2</c>, <c>-3</c>, …
    /// suffix instead. Returns the final file name (not a path).
    /// </summary>
    private string CopyGdsIntoStoreRoot(string gdsPath)
    {
        Directory.CreateDirectory(_userPdkStore.RootDirectory);
        var stem = Path.GetFileNameWithoutExtension(gdsPath);
        var extension = Path.GetExtension(gdsPath);

        for (var n = 1; ; n++)
        {
            var candidateName = n == 1 ? stem + extension : $"{stem}-{n}{extension}";
            var candidatePath = Path.Combine(_userPdkStore.RootDirectory, candidateName);
            if (!File.Exists(candidatePath))
            {
                try
                {
                    File.Copy(gdsPath, candidatePath);
                    return candidateName;
                }
                catch (IOException) when (File.Exists(candidatePath))
                {
                    // Lost the race: another writer created the candidate between the
                    // Exists check and the copy. Fall through to the content compare
                    // below instead of rethrowing. A copy failure that did NOT
                    // produce the candidate (e.g. the source vanished) rethrows.
                }
            }
            if (FilesEqual(gdsPath, candidatePath))
                return candidateName;
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        try
        {
            var firstInfo = new FileInfo(first);
            var secondInfo = new FileInfo(second);
            if (firstInfo.Length != secondInfo.Length)
                return false;

            using var a = firstInfo.OpenRead();
            using var b = secondInfo.OpenRead();
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            int read;
            while ((read = a.Read(bufferA, 0, bufferA.Length)) > 0)
            {
                if (b.Read(bufferB, 0, read) != read)
                    return false;
                if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                    return false;
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            // The candidate vanished between the Exists check and the compare —
            // treat as non-equal so the suffix loop moves on.
            return false;
        }
    }

    // ── Draft filtering / naming ─────────────────────────────────────────────

    /// <summary>
    /// The PDK loader's hard rules a draft must satisfy to round-trip: positive
    /// size and at least one pin (pins within bounds are guaranteed by the
    /// importer). Unpersistable drafts are skipped with a warning — persisting
    /// them would make every later save of the same PDK file fail validation.
    /// </summary>
    private static bool IsPersistable(GdsCellDraft draft, List<string> warnings)
    {
        if (draft.WidthUm <= 0 || draft.HeightUm <= 0)
        {
            warnings.Add($"Cell '{draft.CellName}' was not registered: zero size " +
                         "(the GDS cell has an empty bounding box).");
            return false;
        }
        if (draft.Pins.Count == 0)
        {
            warnings.Add($"Cell '{draft.CellName}' was not registered: no pins detected " +
                         "(a PDK component needs at least one pin).");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Two different GDS cells can sanitize to the same component name; the
    /// store replaces components name-case-insensitively, so later duplicates
    /// get a deterministic <c>_2</c>, <c>_3</c>, … suffix.
    /// </summary>
    private static string DeduplicateName(
        string sanitizedName, string cellName, HashSet<string> usedNames, List<string> warnings)
    {
        var candidate = sanitizedName;
        for (var n = 2; !usedNames.Add(candidate); n++)
            candidate = $"{sanitizedName}_{n}";

        if (!string.Equals(candidate, sanitizedName, StringComparison.Ordinal))
        {
            warnings.Add($"Cell '{cellName}' collides with another imported cell after name " +
                         $"sanitization; registered as '{candidate}' instead of '{sanitizedName}'.");
        }
        return candidate;
    }
}

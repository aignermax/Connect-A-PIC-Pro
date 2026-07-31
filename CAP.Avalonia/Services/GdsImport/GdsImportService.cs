using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Orchestrates a GDS layout import end to end: parse → hierarchy import →
/// map unknown cells to <see cref="PdkComponentDraft"/>s → persist them into a
/// process-agnostic user PDK → register them with the runtime component
/// library. The result (<see cref="GdsImportOutcome"/>) is pure data; turning
/// it into canvas placements is the caller's job (see <see cref="GdsPlacementPlan"/>).
/// <para>
/// Runtime seams are constructor-injected with production defaults, following
/// the codebase's service pattern (cf. <c>PdkImportService</c>): the user-PDK
/// store defaults to the managed root, the template provider feeds the
/// known-component resolver from the loaded library, and the registration
/// callback mirrors <c>LeftPanelViewModel.RegisterSavedCustomComponent</c>
/// (null = skip runtime registration, e.g. headless runs).
/// </para>
/// </summary>
public sealed class GdsImportService
{
    /// <summary>Display-name prefix of the per-file user PDK an import writes ("GDS Import - &lt;file stem&gt;").</summary>
    public const string ImportPdkNamePrefix = "GDS Import - ";

    private readonly UserPdkStore _userPdkStore;
    private readonly Func<IReadOnlyList<ComponentTemplate>>? _templateProvider;
    private readonly Action<PdkComponentDraft, string, string>? _registerComponent;

    /// <summary>Initializes a new <see cref="GdsImportService"/>.</summary>
    /// <param name="userPdkStore">User-PDK persistence; defaults to the managed root under %LocalAppData%.</param>
    /// <param name="templateProvider">
    /// Supplies the currently loaded component templates for known-component
    /// resolution (e.g. <c>() => leftPanel.AllTemplates</c>); null/empty treats
    /// every cell as unknown (all become drafts).
    /// </param>
    /// <param name="registerComponent">
    /// Runtime library registration callback with the same contract as
    /// <c>LeftPanelViewModel.RegisterSavedCustomComponent</c>: (draft, pdkName,
    /// filePath). Null skips runtime registration (persistence still happens).
    /// </param>
    public GdsImportService(
        UserPdkStore? userPdkStore = null,
        Func<IReadOnlyList<ComponentTemplate>>? templateProvider = null,
        Action<PdkComponentDraft, string, string>? registerComponent = null)
    {
        _userPdkStore = userPdkStore ?? UserPdkStore.CreateDefault();
        _templateProvider = templateProvider;
        _registerComponent = registerComponent;
    }

    /// <summary>
    /// Reads the library structure of a GDS file for the import dialog: top-cell
    /// candidates plus a size summary (cell count, per-candidate instance counts).
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable GDS II stream.</exception>
    public async Task<GdsImportAnalysis> AnalyzeAsync(string gdsPath, CancellationToken ct = default)
    {
        var library = await ReadLibraryAsync(gdsPath, ct).ConfigureAwait(false);
        var candidates = library.TopCellCandidates;
        return new GdsImportAnalysis
        {
            LibraryName = library.Name,
            CellCount = library.Cells.Count,
            TopCellCandidates = candidates,
            TopCells = candidates
                .Select(name => new GdsTopCellSummary(name, CountDirectInstances(library, name)))
                .ToList(),
        };
    }

    /// <summary>
    /// Imports <paramref name="topCellName"/> from <paramref name="gdsPath"/>:
    /// unknown cells become registered user-library components; known cells
    /// (matched against the loaded templates) reference existing components.
    /// The source .gds is copied next to the user-PDK JSON (content-aware name
    /// collision handling) so the components' raw code keeps resolving.
    /// </summary>
    /// <param name="gdsPath">Absolute path to the .gds file.</param>
    /// <param name="topCellName">Cell to import; pick from <see cref="AnalyzeAsync"/>.</param>
    /// <param name="options">
    /// Hierarchy import options (mode, pin detection, tolerances). A custom
    /// <see cref="GdsHierarchyImportOptions.ResolveKnownComponent"/> wins over
    /// the template-based resolver when set.
    /// </param>
    /// <param name="progress">Optional user-presentable stage reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not a readable GDS II stream, contains no cells, or does not
    /// define <paramref name="topCellName"/>.
    /// </exception>
    public async Task<GdsImportOutcome> ImportAsync(
        string gdsPath,
        string topCellName,
        GdsHierarchyImportOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report($"Reading '{Path.GetFileName(gdsPath)}'…");
        var library = await ReadLibraryAsync(gdsPath, ct).ConfigureAwait(false);
        ValidateImportTarget(library, gdsPath, topCellName);

        options ??= new GdsHierarchyImportOptions();
        if (options.ResolveKnownComponent is null)
        {
            var templates = _templateProvider?.Invoke() ?? (IReadOnlyList<ComponentTemplate>)Array.Empty<ComponentTemplate>();
            options = options with { ResolveKnownComponent = GdsTemplateResolver.BuildKnownComponentResolver(templates) };
        }

        progress?.Report($"Analyzing hierarchy of '{topCellName}'…");
        var import = await GdsHierarchyImporter.ImportAsync(library, topCellName, options, ct).ConfigureAwait(false);

        var warnings = new List<string>(import.Warnings);
        var persistable = import.ImportedCellDrafts
            .Where(d => IsPersistable(d, warnings))
            .ToList();

        string? gdsFileName = null;
        string? userPdkPath = null;
        var registered = new List<GdsRegisteredComponent>();
        var pdkName = ImportPdkNamePrefix + Path.GetFileNameWithoutExtension(gdsPath);

        if (persistable.Count > 0)
        {
            progress?.Report("Copying the GDS file into the user component library…");
            ct.ThrowIfCancellationRequested();
            gdsFileName = CopyGdsIntoStoreRoot(gdsPath);
            pdkName = ImportPdkNamePrefix + Path.GetFileNameWithoutExtension(gdsFileName);
            var gdsCopyPath = Path.Combine(_userPdkStore.RootDirectory, gdsFileName);

            progress?.Report($"Saving {persistable.Count} component(s) to '{pdkName}'…");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pdkDrafts = new List<PdkComponentDraft>();
            foreach (var cellDraft in persistable)
            {
                ct.ThrowIfCancellationRequested();
                var pdkDraft = GdsCellDraftMapper.Map(cellDraft, gdsCopyPath);
                pdkDraft.Name = DeduplicateName(pdkDraft.Name, cellDraft.CellName, usedNames, warnings);
                userPdkPath = _userPdkStore.SaveToProcessAgnosticNamedPdk(pdkName, pdkDraft, "nazca");
                pdkDrafts.Add(pdkDraft);
                registered.Add(new GdsRegisteredComponent(cellDraft.CellName, pdkDraft.Name));
            }

            if (_registerComponent is not null)
            {
                progress?.Report("Registering components in the library…");
                foreach (var pdkDraft in pdkDrafts)
                    _registerComponent(pdkDraft, pdkName, userPdkPath!);
            }
        }
        else if (import.ImportedCellDrafts.Count > 0)
        {
            warnings.Add("No importable component drafts remained — nothing was registered.");
        }

        return new GdsImportOutcome
        {
            TopCellName = import.TopCellName,
            Mode = import.Mode,
            RegisteredComponents = registered,
            Instances = import.Instances,
            Connections = import.Connections,
            Warnings = warnings,
            UserPdkName = pdkName,
            UserPdkPath = userPdkPath,
            GdsFileName = gdsFileName,
        };
    }

    // ── Stages ───────────────────────────────────────────────────────────────

    private static async Task<GdsLibrary> ReadLibraryAsync(string gdsPath, CancellationToken ct)
    {
        if (!File.Exists(gdsPath))
            throw new FileNotFoundException($"GDS file not found: {gdsPath}", gdsPath);

        try
        {
            await using var stream = new FileStream(
                gdsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            return await new GdsReader().ReadAsync(stream, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(gdsPath)}' could not be read as a GDS II layout: {ex.Message}", ex);
        }
    }

    private static void ValidateImportTarget(GdsLibrary library, string gdsPath, string topCellName)
    {
        var fileName = Path.GetFileName(gdsPath);
        if (library.Cells.Count == 0)
            throw new InvalidDataException($"The file '{fileName}' contains no GDS cells.");
        if (!library.Cells.ContainsKey(topCellName))
        {
            var candidates = library.TopCellCandidates;
            var hint = candidates.Count > 0
                ? $" Top-cell candidates: {string.Join(", ", candidates)}."
                : string.Empty;
            throw new InvalidDataException($"Cell '{topCellName}' does not exist in '{fileName}'.{hint}");
        }
    }

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
                File.Copy(gdsPath, candidatePath);
                return candidateName;
            }
            if (FilesEqual(gdsPath, candidatePath))
                return candidateName;
        }
    }

    private static bool FilesEqual(string first, string second)
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

    private static int CountDirectInstances(GdsLibrary library, string cellName) =>
        library.Cells[cellName].Elements
            .OfType<GdsReference>()
            .Sum(r => r.Columns * r.Rows);
}

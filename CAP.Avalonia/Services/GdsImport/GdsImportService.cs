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
/// <para>
/// Threading: the heavy stages (file read, parse, flatten, pin detection,
/// matching, persistence) run inside <see cref="Task.Run{TResult}"/> so a large
/// file cannot freeze the caller's (UI) thread — and the dialog's Cancel stays
/// clickable. The awaits do NOT use <c>ConfigureAwait(false)</c>: the
/// continuations resume on the caller's context, so the component-registration
/// callback — which mutates UI-bound ObservableCollections — runs exactly where
/// it did before (the same rule <see cref="GdsPlacementExecutor"/> documents
/// for the canvas). The template provider is invoked BEFORE the handoff: it
/// reads UI-bound library collections and must not run on the background
/// thread.
/// </para>
/// </summary>
public sealed partial class GdsImportService
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
    /// Metadata sentinel cells (<see cref="IsMetadataSentinelCell"/>) are filtered
    /// out of the candidates — they carry no layout and only confuse the choice.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not a readable GDS II stream, or nothing but metadata sentinel
    /// cells was found (there is no layout top cell to import).
    /// </exception>
    public static async Task<GdsImportAnalysis> AnalyzeAsync(string gdsPath, CancellationToken ct = default)
    {
        // Parse on a thread-pool thread: the record loop is CPU-bound and would
        // otherwise pin the caller's (UI) context for the whole file.
        var library = await Task.Run(() => ReadLibraryAsync(gdsPath, ct), ct);
        var candidates = library.TopCellCandidates
            .Select(name => UnwrapPassThroughTopCell(library, name))
            .Where(name => !IsMetadataSentinelCell(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidDataException(NoLayoutTopCellMessage(library, gdsPath));
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
    /// True for metadata sentinel cells that must never be offered as layout top
    /// cells. kfactory (the engine behind gdsfactory) writes a
    /// <c>$$$CONTEXT_INFO$$$</c> cell with run metadata into every file; it is
    /// unreferenced, so it floats into the top-candidate list despite holding no
    /// layout. The rule is deliberately conservative: only names wrapped in
    /// <c>$$$</c> on BOTH sides count — the shape layout tools reserve for such
    /// sentinels. A name that merely starts (or ends) with <c>$$$</c> stays a
    /// candidate.
    /// </summary>
    internal static bool IsMetadataSentinelCell(string cellName) =>
        cellName.StartsWith("$$$", StringComparison.Ordinal)
        && cellName.EndsWith("$$$", StringComparison.Ordinal);

    /// <summary>
    /// The analysis-failure message when no importable top cell remains. Names the
    /// sentinel cells when they are the reason — "no candidates" alone would leave
    /// the user staring at a file that visibly contains cells.
    /// </summary>
    private static string NoLayoutTopCellMessage(GdsLibrary library, string gdsPath)
    {
        var fileName = Path.GetFileName(gdsPath);
        return library.Cells.Keys.Any(IsMetadataSentinelCell)
            ? $"'{fileName}' contains no layout top cell: its only top-level cell(s) are metadata " +
              "sentinels (name wrapped in '$$$', e.g. kfactory's '$$$CONTEXT_INFO$$$') — " +
              "there is no layout to import."
            : $"'{fileName}' contains no layout top cell to import.";
    }

    /// <summary>
    /// Looks through a pure pass-through wrapper cell: no geometry of its own and
    /// exactly one element — an untransformed (1×1, unmagnified, unreflected,
    /// unrotated) reference to another cell. Nazca's default export nests every
    /// design under such a wrapper (the default 'nazca' cell), so the wrapper is
    /// the ONLY unreferenced cell and would hide the actual design cell from the
    /// candidate list. The wrapped cell is the more useful import target; the
    /// unwrap repeats while the chain stays trivial (and terminates on cycles or
    /// undefined references by returning the last resolvable name).
    /// </summary>
    private static string UnwrapPassThroughTopCell(GdsLibrary library, string cellName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = cellName;
        while (visited.Add(current)
               && library.Cells.TryGetValue(current, out var cell)
               && cell.Elements.Count == 1
               && cell.Elements[0] is GdsReference reference
               && reference.Columns == 1 && reference.Rows == 1
               && !reference.Reflected
               && Math.Abs(reference.Magnification - 1.0) < 1e-9
               && IsIdentityAngle(reference.AngleDegrees)
               && library.Cells.ContainsKey(reference.CellName))
        {
            current = reference.CellName;
        }
        return current;
    }

    /// <summary>True when the angle is a whole number of turns (an untransformed reference).</summary>
    private static bool IsIdentityAngle(double angleDegrees) =>
        Math.Abs(angleDegrees - (360.0 * Math.Round(angleDegrees / 360.0))) < 1e-9;

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
        options ??= new GdsHierarchyImportOptions();
        options = WithOwnExportPinLayers(options);

        var warnings = new List<string>();
        var infos = new List<string>();

        // UI-bound input, read on the caller's context before the handoff: the
        // template provider touches the loaded library's ObservableCollections.
        if (options.ResolveKnownComponent is null)
        {
            var templates = _templateProvider?.Invoke() ?? (IReadOnlyList<ComponentTemplate>)Array.Empty<ComponentTemplate>();
            options = options with { ResolveKnownComponent = GdsTemplateResolver.BuildKnownComponentResolver(templates, infos) };
        }

        // Heavy stages on a thread-pool thread (see the class remarks); the await
        // resumes on the caller's context, so the registration callback below
        // mutates the UI-bound library exactly where it did before.
        var prepared = await Task.Run(
            () => ParseImportAndPersistAsync(gdsPath, topCellName, options, warnings, infos, progress, ct), ct);

        if (prepared.PdkDrafts.Count > 0 && _registerComponent is not null)
        {
            progress?.Report("Registering components in the library…");
            foreach (var pdkDraft in prepared.PdkDrafts)
                _registerComponent(pdkDraft, prepared.PdkName, prepared.UserPdkPath!);
        }
        else if (prepared.PdkDrafts.Count == 0 && prepared.Import.ImportedCellDrafts.Count > 0)
        {
            warnings.Add("No importable component drafts remained — nothing was registered.");
        }

        return new GdsImportOutcome
        {
            TopCellName = prepared.Import.TopCellName,
            Mode = prepared.Import.Mode,
            RegisteredComponents = prepared.Registered,
            Instances = prepared.Import.Instances,
            Connections = prepared.Import.Connections,
            TopCellWaveguidePolygons = prepared.Import.TopCellWaveguidePolygons,
            Warnings = warnings,
            Infos = infos,
            UserPdkName = prepared.PdkName,
            UserPdkPath = prepared.UserPdkPath,
            GdsFileName = prepared.GdsFileName,
        };
    }

    /// <summary>
    /// The off-thread body of <see cref="ImportAsync"/>: read → validate →
    /// hierarchy import → persist. Pure data and file IO only — nothing here may
    /// touch UI-bound state (the registration callback stays with the caller).
    /// </summary>
    private async Task<PreparedImport> ParseImportAndPersistAsync(
        string gdsPath,
        string topCellName,
        GdsHierarchyImportOptions options,
        List<string> warnings,
        List<string> infos,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"Reading '{Path.GetFileName(gdsPath)}'…");
        var library = await ReadLibraryAsync(gdsPath, ct);
        ValidateImportTarget(library, gdsPath, topCellName);

        progress?.Report($"Analyzing hierarchy of '{topCellName}'…");
        var import = await GdsHierarchyImporter.ImportAsync(library, topCellName, options, ct);

        warnings.AddRange(import.Warnings);
        infos.AddRange(import.Infos);
        var persistable = import.ImportedCellDrafts
            .Where(d => IsPersistable(d, warnings))
            .ToList();

        var pdkName = ImportPdkNamePrefix + Path.GetFileNameWithoutExtension(gdsPath);
        var pdkDrafts = new List<PdkComponentDraft>();
        var registered = new List<GdsRegisteredComponent>();
        string? gdsFileName = null;
        string? userPdkPath = null;

        if (persistable.Count > 0)
        {
            progress?.Report("Copying the GDS file into the user component library…");
            ct.ThrowIfCancellationRequested();
            gdsFileName = CopyGdsIntoStoreRoot(gdsPath);
            pdkName = _userPdkStore.ResolveAvailablePdkName(
                ImportPdkNamePrefix + Path.GetFileNameWithoutExtension(gdsFileName));
            var gdsCopyPath = Path.Combine(_userPdkStore.RootDirectory, gdsFileName);

            progress?.Report($"Saving {persistable.Count} component(s) to '{pdkName}'…");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < persistable.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cellDraft = persistable[i];
                var pdkDraft = GdsCellDraftMapper.Map(cellDraft, gdsCopyPath, warnings);
                pdkDraft.Name = DeduplicateName(pdkDraft.Name, cellDraft.CellName, usedNames, warnings);
                pdkDrafts.Add(pdkDraft);
                registered.Add(new GdsRegisteredComponent(cellDraft.CellName, pdkDraft.Name));
                if ((i + 1) % 100 == 0 && i + 1 < persistable.Count)
                    progress?.Report($"Saving components to '{pdkName}'… {i + 1}/{persistable.Count}");
            }

            // One load-modify-save for the whole batch: the per-draft variant
            // rewrites the entire PDK file on every call (O(n²) — it dominated
            // large imports). All-or-nothing also means a cancelled import never
            // leaves a half-written PDK behind.
            userPdkPath = _userPdkStore.SaveAllToProcessAgnosticNamedPdk(pdkName, pdkDrafts, "nazca");
        }

        return new PreparedImport
        {
            Import = import,
            PdkDrafts = pdkDrafts,
            Registered = registered,
            PdkName = pdkName,
            UserPdkPath = userPdkPath,
            GdsFileName = gdsFileName,
        };
    }

    /// <summary>
    /// Intermediate result of the off-thread stages: the hierarchy import plus
    /// the persisted drafts awaiting runtime registration on the caller's
    /// context.
    /// </summary>
    private sealed record PreparedImport
    {
        /// <summary>The hierarchy import result (drafts, instances, connections).</summary>
        public required GdsCircuitImport Import { get; init; }

        /// <summary>Persisted PDK drafts, in persist order (empty when nothing was importable).</summary>
        public required List<PdkComponentDraft> PdkDrafts { get; init; }

        /// <summary>Cell-name → registered-component-name pairs, parallel to <see cref="PdkDrafts"/>.</summary>
        public required List<GdsRegisteredComponent> Registered { get; init; }

        /// <summary>Final (collision-resolved) user-PDK name.</summary>
        public required string PdkName { get; init; }

        /// <summary>Path of the user-PDK JSON the drafts were saved to, or null when nothing persisted.</summary>
        public string? UserPdkPath { get; init; }

        /// <summary>File name of the .gds copy in the user-PDK root, or null when nothing persisted.</summary>
        public string? GdsFileName { get; init; }
    }

    /// <summary>
    /// nazca demofab's black-box pin-label layer (<c>bb_pin_text</c> in demofab's
    /// layer table): our own Nazca export places demofab cells (the bundled Demo
    /// PDK) whose pin names live only there.
    /// </summary>
    private static readonly (int Layer, int Datatype) DemofabPinTextLayer = (501, 1);

    /// <summary>
    /// Adds the pin-label layers of our OWN exports to the caller's pin detection
    /// (currently demofab's <c>bb_pin_text</c> — the detector default already
    /// carries it). The import dialog's layer fields default to the gdsfactory
    /// convention and always pass an explicit list, which would silently drop
    /// our own format: re-importing a Lunima-exported GDS must work out of the
    /// box, so the layer is unioned in here rather than trusting any default.
    /// </summary>
    private static GdsHierarchyImportOptions WithOwnExportPinLayers(GdsHierarchyImportOptions options)
    {
        if (options.PinDetection.PortLayers.Contains(DemofabPinTextLayer))
            return options;
        return options with
        {
            PinDetection = options.PinDetection with
            {
                PortLayers = [.. options.PinDetection.PortLayers, DemofabPinTextLayer],
            },
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

    private static int CountDirectInstances(GdsLibrary library, string cellName) =>
        library.Cells[cellName].Elements
            .OfType<GdsReference>()
            .Sum(r => r.Columns * r.Rows);
}

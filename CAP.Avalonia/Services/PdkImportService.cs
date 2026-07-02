using System.Text.Json;
using System.Text.Json.Serialization;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services;

/// <summary>
/// Orchestrates PDK import from Nazca Python .py files.
/// Handles module name extraction, PYTHONPATH setup, and conversion to PDK JSON format.
/// Issue #476: PDK Import Wizard with Python/Nazca parser and AI-assisted error correction.
/// </summary>
public class PdkImportService
{
    private readonly UserPreferencesService _preferences;
    private readonly CAP_Core.Export.ProcessLaunchFactory _launchFactory;

    /// <summary>Initializes a new <see cref="PdkImportService"/>.</summary>
    /// <param name="preferences">User preferences service for Python path resolution.</param>
    /// <param name="launchFactory">Factory for launching the Nazca parser subprocess.</param>
    public PdkImportService(UserPreferencesService preferences, CAP_Core.Export.ProcessLaunchFactory? launchFactory = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _launchFactory = launchFactory ?? CAP_Core.Export.ProcessLaunchFactory.CreateDefault();
    }

    /// <summary>
    /// Parses a Nazca Python PDK module from a .py file path.
    /// Adds the file's directory to PYTHONPATH so the module can be imported.
    /// </summary>
    /// <param name="pyFilePath">Absolute path to the .py module file.</param>
    /// <param name="progress">Optional progress reporter for status updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed PDK component data.</returns>
    /// <exception cref="FileNotFoundException">When the .py file does not exist.</exception>
    /// <exception cref="PdkParserException">When parsing the module fails.</exception>
    public async Task<PdkParseResult> ParseFromFileAsync(
        string pyFilePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pyFilePath))
            throw new FileNotFoundException($"Python file not found: {pyFilePath}", pyFilePath);

        var directory = Path.GetDirectoryName(pyFilePath) ?? "";
        var moduleName = Path.GetFileNameWithoutExtension(pyFilePath);
        var scriptPath = FindParserScript();
        var pythonExecutable = _preferences.GetCustomPythonPath() ?? "python3";

        var parser = new PdkNazcaParserService(pythonExecutable, scriptPath, launchFactory: _launchFactory);
        progress?.Report($"Parsing module '{moduleName}'...");

        return await parser.ParseAsync(moduleName, null, directory, cancellationToken);
    }

    /// <summary>
    /// Converts a <see cref="PdkParseResult"/> to a <see cref="PdkDraft"/> for JSON serialization.
    /// Components from the parse result are mapped to PDK draft format.
    /// </summary>
    /// <param name="result">The parsed PDK data from the Python script.</param>
    /// <returns>A <see cref="PdkDraft"/> ready for JSON serialization.</returns>
    public PdkDraft ConvertToPdkDraft(PdkParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var effectiveName = !string.IsNullOrWhiteSpace(result.Name)
            ? result.Name
            : result.NazcaModuleName ?? "Imported PDK";

        var draft = new PdkDraft
        {
            FileFormatVersion = result.FileFormatVersion,
            Name = effectiveName,
            Description = result.Description,
            Foundry = result.Foundry,
            Version = result.Version,
            DefaultWavelengthNm = result.DefaultWavelengthNm,
            NazcaModuleName = result.NazcaModuleName,
        };

        foreach (var comp in result.Components)
            draft.Components.Add(ConvertComponent(comp));

        return draft;
    }

    /// <summary>
    /// Serializes a <see cref="PdkDraft"/> to indented JSON and writes it to the specified path.
    /// </summary>
    /// <param name="draft">The PDK draft to serialize.</param>
    /// <param name="outputPath">Target file path for the JSON output.</param>
    public async Task SaveToJsonAsync(PdkDraft draft, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.Serialize(draft, options);
        await File.WriteAllTextAsync(outputPath, json);
    }

    /// <summary>
    /// Searches for the parse_pdk.py script relative to the application base directory.
    /// Throws immediately with the full list of probed paths when nothing is found —
    /// previously this returned the first candidate and deferred the error to
    /// <see cref="PdkNazcaParserService"/>, which produced a misleading
    /// FileNotFoundException pointing at a path the user never chose.
    /// </summary>
    private static string FindParserScript()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "scripts", "parse_pdk.py"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "scripts", "parse_pdk.py")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "scripts", "parse_pdk.py")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "scripts", "parse_pdk.py")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "PDK parser script not found. Looked in:" + Environment.NewLine +
            string.Join(Environment.NewLine, candidates.Select(c => "  - " + c)),
            candidates[0]);
    }

    private static PdkComponentDraft ConvertComponent(ParsedComponentGeometry comp)
    {
        var draft = new PdkComponentDraft
        {
            Name = comp.Name,
            Category = comp.Category,
            NazcaFunction = comp.NazcaFunction,
            NazcaParameters = comp.NazcaParameters,
            WidthMicrometers = comp.WidthMicrometers,
            HeightMicrometers = comp.HeightMicrometers,
            NazcaOriginOffsetX = comp.NazcaOriginOffsetX,
            NazcaOriginOffsetY = comp.NazcaOriginOffsetY,
        };

        foreach (var pin in comp.Pins)
            draft.Pins.Add(ConvertPin(pin));

        return draft;
    }

    private static PhysicalPinDraft ConvertPin(ParsedPinGeometry pin)
    {
        return new PhysicalPinDraft
        {
            Name = pin.Name,
            OffsetXMicrometers = pin.OffsetXMicrometers,
            OffsetYMicrometers = pin.OffsetYMicrometers,
            AngleDegrees = pin.AngleDegrees,
        };
    }
}

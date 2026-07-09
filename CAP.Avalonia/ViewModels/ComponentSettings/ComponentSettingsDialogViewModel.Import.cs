using CAP.Avalonia.Services;
using CAP_DataAccess.Import;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// S-parameter import helpers of the Component Settings dialog: importer selection,
/// port-name reconciliation against the component's pins, and the human-readable
/// import status line. Split out to keep the main ViewModel file focused.
/// </summary>
public partial class ComponentSettingsDialogViewModel
{
    /// <summary>
    /// Returns <paramref name="imported"/> unchanged when port names already
    /// align, the result of <see cref="PortNameMapping.Remap"/> with a
    /// user-supplied mapping when they don't, or <c>null</c> when the user
    /// cancelled the mapping dialog (in which case <see cref="StatusText"/>
    /// is set so the caller can return without storing anything).
    /// </summary>
    private async Task<ImportedSParameters?> ReconcilePortNamesAsync(ImportedSParameters imported)
    {
        if (_availablePinNames == null || _availablePinNames.Count == 0)
            return imported; // caller didn't tell us the pin names — proceed and let Apply complain if anything's wrong

        if (PortNameMapping.NamesAlignWithComponent(imported.PortNames, _availablePinNames))
            return imported;

        if (imported.PortNames.Count != _availablePinNames.Count)
        {
            // Different port counts is structurally unmappable — bail out
            // loudly rather than open a dialog the user couldn't satisfy.
            StatusText = $"Cannot import: file has {imported.PortNames.Count} port(s), " +
                         $"but '{_displayName}' has {_availablePinNames.Count} pin(s).";
            return null;
        }

        if (_portMappingDialog == null)
        {
            // No interactive surface available (typically test or headless).
            StatusText = $"Imported port names don't match component pins on '{_displayName}'. " +
                         $"Re-run with a port-mapping dialog wired up to resolve this interactively.";
            return null;
        }

        var mapping = await _portMappingDialog.ShowAsync(_displayName, imported.PortNames, _availablePinNames);
        if (mapping == null)
        {
            StatusText = "Import cancelled — no port mapping was confirmed.";
            return null;
        }

        return PortNameMapping.Remap(imported, mapping);
    }

    private static string BuildImportStatus(
        string path,
        ImportedSParameters imported,
        ApplyResult? applyResult)
    {
        var fileName = Path.GetFileName(path);
        var portInfo = $"{imported.PortCount} ports, {imported.SMatricesByWavelengthNm.Count} wavelengths";

        if (applyResult == null)
            return $"Imported {portInfo} from '{fileName}'.";

        if (applyResult.IsTotalFailure)
            return $"Imported {portInfo} from '{fileName}', but no wavelength could be applied to the live component (see Error Console).";

        if (applyResult.IsPartial)
            return $"Imported {portInfo}; applied {applyResult.Applied} of {applyResult.Applied + applyResult.Skipped.Count} wavelength(s) — {applyResult.Skipped.Count} skipped (see Error Console).";

        var replacedNote = applyResult.Replaced > 0 ? $" ({applyResult.Replaced} replaced)" : "";
        return $"Imported {portInfo} from '{fileName}'; applied {applyResult.Applied} wavelength(s){replacedNote}.";
    }

    private ISParameterImporter? FindImporter(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".txt")
            return LooksLikeLumericalTxt(path) ? _importers.First(i => i is LumericalSParameterImporter) : null;
        return _importers.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
    }

    private bool LooksLikeLumericalTxt(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
                    continue;
                if (trimmed.StartsWith('('))
                    return true;
                var tokens = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return tokens.Length >= 9 &&
                       double.TryParse(tokens[0], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out _);
            }
        }
        catch (IOException ex)
        {
            _errorConsole?.LogWarning($"Could not probe '{path}' for Lumerical .txt format: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _errorConsole?.LogWarning($"Could not probe '{path}' for Lumerical .txt format: {ex.Message}");
        }
        return false;
    }
}

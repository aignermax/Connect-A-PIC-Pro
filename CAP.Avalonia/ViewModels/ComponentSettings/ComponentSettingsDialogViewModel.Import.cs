using CAP.Avalonia.Services;
using CAP_DataAccess.Import;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

public partial class ComponentSettingsDialogViewModel
{
    private async Task<ImportedSParameters?> ReconcilePortNamesAsync(ImportedSParameters imported)
    {
        if (_availablePinNames == null || _availablePinNames.Count == 0)
            return imported;

        if (PortNameMapping.NamesAlignWithComponent(imported.PortNames, _availablePinNames))
            return imported;

        if (imported.PortNames.Count != _availablePinNames.Count)
        {
            StatusText = $"Cannot import: file has {imported.PortNames.Count} port(s), " +
                         $"but '{_displayName}' has {_availablePinNames.Count} pin(s).";
            return null;
        }

        if (_portMappingDialog == null)
        {
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

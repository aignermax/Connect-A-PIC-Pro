using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
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
            StatusText = string.Format(
                LocalizationService.Instance.Translate("CompSettings.CannotImportPortCount"),
                imported.PortNames.Count, _displayName, _availablePinNames.Count);
            return null;
        }

        if (_portMappingDialog == null)
        {
            StatusText = string.Format(
                LocalizationService.Instance.Translate("CompSettings.NoPortMappingDialog"), _displayName);
            return null;
        }

        var mapping = await _portMappingDialog.ShowAsync(_displayName, imported.PortNames, _availablePinNames);
        if (mapping == null)
        {
            StatusText = LocalizationService.Instance.Translate("CompSettings.ImportCancelledNoMapping");
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
        var portInfo = string.Format(
            LocalizationService.Instance.Translate("CompSettings.PortInfo"),
            imported.PortCount, imported.SMatricesByWavelengthNm.Count);

        if (applyResult == null)
            return string.Format(LocalizationService.Instance.Translate("CompSettings.Imported"), portInfo, fileName);

        if (applyResult.IsTotalFailure)
            return string.Format(
                LocalizationService.Instance.Translate("CompSettings.ImportedNoneApplied"), portInfo, fileName);

        if (applyResult.IsPartial)
            return string.Format(
                LocalizationService.Instance.Translate("CompSettings.ImportedPartial"),
                portInfo, applyResult.Applied, applyResult.Applied + applyResult.Skipped.Count, applyResult.Skipped.Count);

        var replacedNote = applyResult.Replaced > 0
            ? string.Format(LocalizationService.Instance.Translate("CompSettings.ReplacedNote"), applyResult.Replaced)
            : "";
        return string.Format(
            LocalizationService.Instance.Translate("CompSettings.ImportedApplied"),
            portInfo, fileName, applyResult.Applied, replacedNote);
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

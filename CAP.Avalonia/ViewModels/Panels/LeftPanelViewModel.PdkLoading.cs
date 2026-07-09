using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// PDK loading concern of <see cref="LeftPanelViewModel"/>: the "Load PDK" command
/// (.json directly, .py via the Import Wizard), the shared JSON registration path,
/// and the startup restore of user-imported PDKs recorded in preferences (issue #700).
/// </summary>
public partial class LeftPanelViewModel
{
    [RelayCommand]
    private async Task LoadPdk()
    {
        if (FileDialogService == null) return;

        var filePath = await FileDialogService.ShowOpenFileDialogAsync(
            "Open PDK",
            "PDK Files (*.json;*.py)|*.json;*.py|PDK JSON (*.json)|*.json|Nazca Python (*.py)|*.py|All Files (*.*)|*.*");

        if (string.IsNullOrEmpty(filePath)) return;

        // Python file: open the Import Wizard to parse and convert it first
        if (filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            await LoadPdkFromPythonFileAsync(filePath);
            return;
        }

        LoadPdkFromJsonFile(filePath, _pdkLoader.LoadFromFile);
    }

    private async Task LoadPdkFromPythonFileAsync(string pyFilePath)
    {
        if (ShowImportWizardAsync == null)
        {
            UpdateStatus?.Invoke("PDK Import Wizard is not available in this context.");
            return;
        }

        UpdateStatus?.Invoke($"Opening PDK Import Wizard for '{Path.GetFileName(pyFilePath)}'...");
        var savedJsonPath = await ShowImportWizardAsync(pyFilePath);

        if (string.IsNullOrEmpty(savedJsonPath)) return; // User cancelled

        LoadPdkFromJsonFile(savedJsonPath, _pdkLoader.LoadFromFile);
    }

    /// <summary>
    /// Reloads the user-imported PDKs recorded in preferences so they survive an app
    /// restart (issue #700). Paths whose file no longer exists are skipped and pruned
    /// from preferences. Uses the edit-tolerant loader because user PDKs created via
    /// the "New Component" feature may still lack Nazca origin offsets (issue #656).
    /// </summary>
    internal void RestoreUserPdks()
    {
        foreach (var path in _preferencesService.GetUserPdkPaths())
        {
            if (!File.Exists(path))
            {
                _preferencesService.RemoveUserPdkPath(path);
                _errorConsole?.LogWarning(
                    $"User PDK '{path}' no longer exists — removed from auto-load list.");
                continue;
            }

            LoadPdkFromJsonFile(path, _pdkLoader.LoadFromFileForEditing);
        }
    }

    /// <summary>
    /// Loads a PDK JSON file into the library: converts its components to templates,
    /// registers it with the PDK manager, records its path for auto-reload, and
    /// re-applies the active process lock (#570). <paramref name="loadPdk"/> selects
    /// the strict or edit-tolerant loader.
    /// </summary>
    private void LoadPdkFromJsonFile(string filePath, Func<string, PdkDraft> loadPdk)
    {
        if (PdkManager.IsPdkLoaded(filePath))
        {
            UpdateStatus?.Invoke("PDK already loaded from this file");
            return;
        }

        try
        {
            var pdk = loadPdk(filePath);

            if (PdkManager.IsPdkNameLoaded(pdk.Name, null))
            {
                UpdateStatus?.Invoke($"PDK '{pdk.Name}' is already loaded");
                return;
            }

            _loadedPdkDrafts.Add(pdk);

            int addedCount = 0;
            foreach (var pdkComp in pdk.Components)
            {
                var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName);
                AllTemplates.Add(template);
                if (!Categories.Contains(template.Category))
                    Categories.Add(template.Category);
                addedCount++;
            }

            PdkManager.RegisterPdk(pdk.Name, filePath, false, addedCount);
            _preferencesService.AddUserPdkPath(filePath);

            // A PDK imported while a process is locked must not escape the lock:
            // re-apply so a foreign PDK registers disabled (issue #570).
            ReapplyActiveProcessAfterPdkChange();
            FilterComponents();
            UpdateStatus?.Invoke($"Loaded PDK '{pdk.Name}' with {addedCount} components");
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to load PDK: {ex.Message}", ex);
            UpdateStatus?.Invoke($"Failed to load PDK: {ex.Message}");
        }
    }

    private static ComponentTemplate ConvertPdkComponentToTemplate(
        PdkComponentDraft pdkComp, string pdkName, string? nazcaModuleName,
        string? gdsFactoryRoutingCrossSection = null)
        => PdkTemplateConverter.ConvertToTemplate(
            pdkComp, pdkName, nazcaModuleName, gdsFactoryRoutingCrossSection);
}

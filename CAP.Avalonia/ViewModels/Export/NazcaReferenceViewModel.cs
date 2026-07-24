using System.Text;
using CAP_Core.Export;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// ViewModel for the Nazca Reference Script Generator panel.
/// Allows users to generate a ground-truth Python/Nazca script for
/// validating GDS export coordinates (Issue #329 debugging).
/// </summary>
public partial class NazcaReferenceViewModel : ObservableObject
{
    private readonly NazcaReferenceGenerator _generator = new();

    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("Analysis.Nazca.InitialStatus");

    [ObservableProperty]
    private string _coordinatesText = string.Empty;

    [ObservableProperty]
    private bool _hasScript;

    [ObservableProperty]
    private string _scriptPath = string.Empty;

    /// <summary>
    /// File dialog service for selecting output path. Set by parent ViewModel.
    /// </summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Generates the reference Nazca Python script and saves it to a user-chosen path.
    /// </summary>
    [RelayCommand]
    public async Task GenerateScriptAsync()
    {
        var path = await ResolveOutputPath();
        if (string.IsNullOrEmpty(path))
            return;

        var coordsPath  = Path.ChangeExtension(path, ".coords.json");
        var gdsPath     = Path.ChangeExtension(path, ".gds");
        var scriptContent = _generator.GeneratePythonScript(gdsPath, coordsPath);

        await File.WriteAllTextAsync(path, scriptContent, Encoding.UTF8);

        ScriptPath = path;
        HasScript  = true;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("Analysis.Nazca.Saved"), Path.GetFileName(path));
        RefreshCoordinatesText();
    }

    /// <summary>
    /// Refreshes the expected coordinates display from the generator.
    /// </summary>
    private void RefreshCoordinatesText()
    {
        var coords = _generator.GetExpectedCoordinates();
        var sb = new StringBuilder();
        sb.AppendLine("Expected ground-truth coordinates (physical µm):");
        foreach (var (key, (x, y)) in coords)
            sb.AppendLine($"  {key}: ({x:F1}, {y:F1})");

        sb.AppendLine();
        sb.AppendLine("Nazca placement coordinates (Y-flipped):");
        var nazcaCoords = _generator.GetExpectedNazcaCoordinates();
        foreach (var (key, (x, y)) in nazcaCoords)
            sb.AppendLine($"  {key}: ({x:F1}, {y:F1})");

        CoordinatesText = sb.ToString();
    }

    /// <summary>
    /// Resolves the output path, using a file dialog if available.
    /// </summary>
    private async Task<string?> ResolveOutputPath()
    {
        if (FileDialogService != null)
        {
            return await FileDialogService.ShowSaveFileDialogAsync(
                "Save Reference Nazca Script",
                ".py",
                "Python Files|*.py|All Files|*.*");
        }

        // Fallback: save to temp directory
        return Path.Combine(Path.GetTempPath(), "reference_nazca.py");
    }
}

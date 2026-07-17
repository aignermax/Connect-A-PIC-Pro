using System.Collections.ObjectModel;
using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Diagnostics;

/// <summary>
/// ViewModel for end-to-end Nazca export validation.
/// Validates that exported code matches UI design exactly.
/// </summary>
public partial class ExportValidationViewModel : ObservableObject
{
    [ObservableProperty]
    private string _validationStatus = LocalizationService.Instance.Translate("Diag.Ready");

    [ObservableProperty]
    private bool _isValid = false;

    [ObservableProperty]
    private int _totalChecks = 0;

    [ObservableProperty]
    private int _passedChecks = 0;

    [ObservableProperty]
    private int _failedChecks = 0;

    [ObservableProperty]
    private int _warningCount = 0;

    [ObservableProperty]
    private bool _hasResults = false;

    public ObservableCollection<ValidationMessage> Messages { get; } = new();

    private readonly SimpleNazcaExporter _exporter;
    private readonly ExportValidator _validator;
    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Initializes a new instance of <see cref="ExportValidationViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public ExportValidationViewModel(ErrorConsoleService? errorConsole = null)
    {
        _exporter = new SimpleNazcaExporter();
        _validator = new ExportValidator();
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Runs end-to-end validation on the current design.
    /// </summary>
    [RelayCommand]
    public void RunValidation(DesignCanvasViewModel? canvas)
    {
        if (canvas == null)
        {
            ValidationStatus = LocalizationService.Instance.Translate("Diag.Export.NoDesign");
            return;
        }

        Messages.Clear();
        ValidationStatus = LocalizationService.Instance.Translate("Diag.Export.Running");

        try
        {
            // Export to Nazca code
            var nazcaCode = _exporter.Export(canvas);

            // Collect components and connections
            var components = canvas.Components.Select(vm => vm.Component).ToList();
            var connections = canvas.Connections.Select(vm => vm.Connection).ToList();

            var result = _validator.Validate(components, connections, nazcaCode, null);

            // Update UI with results
            DisplayResults(result);
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Export validation failed: {ex.Message}", ex);
            ValidationStatus = string.Format(LocalizationService.Instance.Translate("Diag.Export.Failed"), ex.Message);
            HasResults = false;
        }
    }

    /// <summary>
    /// Displays validation results in the UI.
    /// </summary>
    private void DisplayResults(ValidationResult result)
    {
        IsValid = result.IsValid;
        TotalChecks = result.TotalChecks;
        PassedChecks = result.PassedChecks;
        FailedChecks = result.FailedChecks;
        WarningCount = result.WarningCount;

        if (result.IsValid)
        {
            ValidationStatus = string.Format(
                LocalizationService.Instance.Translate("Diag.Export.Passed"), PassedChecks, TotalChecks);
        }
        else
        {
            ValidationStatus = string.Format(
                LocalizationService.Instance.Translate("Diag.Export.FailedSummary"), FailedChecks, WarningCount);
        }

        // Add errors
        foreach (var error in result.Errors)
        {
            Messages.Add(new ValidationMessage
            {
                Severity = "Error",
                Message = error
            });
        }

        // Add warnings
        foreach (var warning in result.Warnings)
        {
            Messages.Add(new ValidationMessage
            {
                Severity = "Warning",
                Message = warning
            });
        }

        // Add successes (only show first 10 to avoid clutter)
        var successesToShow = result.Successes.Take(10).ToList();
        foreach (var success in successesToShow)
        {
            Messages.Add(new ValidationMessage
            {
                Severity = "Success",
                Message = success
            });
        }

        if (result.Successes.Count > 10)
        {
            Messages.Add(new ValidationMessage
            {
                Severity = "Info",
                Message = string.Format(
                    LocalizationService.Instance.Translate("Diag.Export.MoreChecks"),
                    result.Successes.Count - 10)
            });
        }

        HasResults = true;
    }

    /// <summary>
    /// Clears validation results.
    /// </summary>
    [RelayCommand]
    public void ClearResults()
    {
        Messages.Clear();
        ValidationStatus = LocalizationService.Instance.Translate("Diag.Ready");
        IsValid = false;
        TotalChecks = 0;
        PassedChecks = 0;
        FailedChecks = 0;
        WarningCount = 0;
        HasResults = false;
    }
}

/// <summary>
/// Represents a single validation message.
/// </summary>
public class ValidationMessage
{
    public string Severity { get; init; } = "Info"; // "Error", "Warning", "Success", "Info"
    public string Message { get; init; } = "";
}

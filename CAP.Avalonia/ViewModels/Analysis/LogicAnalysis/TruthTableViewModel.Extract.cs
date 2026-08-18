using System.Globalization;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Extraction half of <see cref="TruthTableViewModel"/>: runs the
/// <see cref="TruthTableExtractor"/> asynchronously with cancellation and maps the
/// result (or any validation failure) onto the panel's display state.
/// </summary>
public partial class TruthTableViewModel
{
    /// <summary>Extracts the truth table for the current pin assignment.</summary>
    [RelayCommand]
    private async Task Extract()
    {
        if (IsProcessing || _group == null)
            return;

        var inputs = InputPins.Where(p => p.IsChecked).Select(p => p.PinName).ToArray();
        var outputs = OutputPins.Where(p => p.IsChecked).Select(p => p.PinName).ToArray();
        var biases = BiasPins.Where(p => p.IsChecked).Select(p => p.PinName).ToArray();
        if (inputs.Length == 0 || outputs.Length == 0)
        {
            StatusText = Translate("Analysis.TruthTable.SelectPins");
            return;
        }

        _extractCts = new CancellationTokenSource();
        IsProcessing = true;
        StatusText = Translate("Analysis.TruthTable.Running");
        try
        {
            var table = await _extractor.ExtractAsync(
                _group, inputs, outputs, biases, Threshold, ResolveWavelengthNm(), _extractCts.Token);
            ShowTable(table);
            // Persist the winning assignment on the group (issue #981) so the next
            // save → load round trip prefills the panel — a cancelled or failed
            // extraction must not overwrite the last good one.
            _group.TruthTablePinAssignment = new TruthTablePinAssignment
            {
                InputPinNames = inputs.ToList(),
                OutputPinNames = outputs.ToList(),
                BiasPinNames = biases.ToList(),
                Threshold = Threshold
            };
            StatusText = string.Format(Translate("Analysis.TruthTable.Complete"), table.Rows.Count);
        }
        catch (OperationCanceledException)
        {
            HasResult = false;
            Rows.Clear();
            StatusText = Translate("Analysis.TruthTable.Cancelled");
        }
        catch (ArgumentException ex)
        {
            // Extractor validation (unknown pin, pin both in/out, threshold outside
            // (0,1)…) becomes a readable message instead of a crash.
            HasResult = false;
            Rows.Clear();
            StatusText = string.Format(Translate("Analysis.TruthTable.Failed"), ex.Message);
        }
        catch (Exception ex)
        {
            // Unexpected simulation failure: never escape the async command and leave
            // a stale "Extracting…" status behind.
            HasResult = false;
            Rows.Clear();
            StatusText = string.Format(Translate("Analysis.TruthTable.Failed"), ex.Message);
        }
        finally
        {
            IsProcessing = false;
            _extractCts?.Dispose();
            _extractCts = null;
        }
    }

    /// <summary>Cancels the running extraction.</summary>
    [RelayCommand]
    private void Cancel() => _extractCts?.Cancel();

    private void ShowTable(TruthTable table)
    {
        Rows.Clear();
        InputHeaders.Clear();
        OutputHeaders.Clear();
        foreach (var name in table.InputPinNames)
            InputHeaders.Add(name);
        foreach (var name in table.OutputPinNames)
            OutputHeaders.Add(name);

        foreach (var row in table.Rows)
        {
            var inputBits = table.InputPinNames.Select(name => row.InputBits[name] ? "1" : "0");
            var cells = table.OutputPinNames
                .Select(name => ToCell(row.Outputs[name]))
                .ToArray();
            Rows.Add(new TruthTableRowViewModel(string.Join(" ", inputBits), cells));
        }

        BiasSummaryText = table.BiasPinNames.Count > 0
            ? string.Format(Translate("TruthTable.BiasSummary"), string.Join(", ", table.BiasPinNames))
            : "";
        HasResult = true;
    }

    private static TruthTableOutputCellViewModel ToCell(LogicOutputValue value) =>
        new(value.IsOne, value.Power.ToString("F2", CultureInfo.InvariantCulture));
}

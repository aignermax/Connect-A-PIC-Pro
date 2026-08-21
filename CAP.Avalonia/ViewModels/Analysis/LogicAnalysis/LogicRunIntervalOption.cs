using System.Globalization;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// One selectable auto-clock rate of the Logic panel's Run mode (issue #1111): the
/// tick interval plus its localized display label (e.g. "0.5 s per clock"). The
/// cadence is didactic, not to scale — the real gates settle in picoseconds.
/// </summary>
public sealed class LogicRunIntervalOption
{
    /// <summary>Initializes the option with its tick interval; the label is built in the active language.</summary>
    public LogicRunIntervalOption(TimeSpan interval)
    {
        Interval = interval;
        Label = string.Format(
            LocalizationService.Instance.Translate("LogicPanel.RunIntervalFormat"),
            interval.TotalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The tick interval this option requests from the run clock.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Localized display text (e.g. "0.5 s per clock").</summary>
    public string Label { get; }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

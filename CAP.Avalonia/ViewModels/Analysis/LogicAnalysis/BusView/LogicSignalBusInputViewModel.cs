using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

/// <summary>
/// One input bus of the Logic panel's bus view (issue #1068, NAND game rung 5): the
/// header row for an indexed input family like the 4-bit adder's <c>A0</c>–<c>A3</c>,
/// showing the family's decimal value (<c>A = 5 (0101)</c>) and offering an editable
/// decimal quick-set field. Setting the field writes the member toggles — index 0 is
/// the least-significant bit — and out-of-range input clamps to the family's maximum.
/// The member toggles stay visible beneath the header, so individual bits remain
/// reachable. Display-level only: the network itself is untouched.
/// </summary>
public partial class LogicSignalBusInputViewModel : LogicInputRowViewModel
{
    private readonly (int Index, LogicNetworkInputViewModel Member)[] _byIndex;
    private bool _updatingValue;

    /// <summary>Groups the <paramref name="members"/> of one indexed input family under <paramref name="prefix"/>.</summary>
    public LogicSignalBusInputViewModel(string prefix, IEnumerable<LogicNetworkInputViewModel> members)
    {
        Prefix = prefix;
        _byIndex = members
            .Select(m => SignalBusName.TrySplit(m.PinName, out _, out var index) ? (index, m) : (0, m))
            .OrderBy(pair => pair.Item1)
            .ToArray();
        Members = _byIndex.Select(pair => pair.Member).ToList();
        MaxValue = _byIndex.Sum(pair => 1L << pair.Index);
        foreach (var member in Members)
            member.PropertyChanged += OnMemberChanged;
        RefreshFromMembers();
    }

    /// <summary>The shared name prefix of the family ("A" for A0–A3).</summary>
    public string Prefix { get; }

    /// <summary>
    /// The highest decimal value the member indices can represent — 2^width − 1 for a
    /// contiguous family; typed input clamps to it.
    /// </summary>
    public long MaxValue { get; }

    /// <summary>The member toggles in index order (index 0 = least-significant bit first).</summary>
    public IReadOnlyList<LogicNetworkInputViewModel> Members { get; }

    /// <summary>Header line for the bus, e.g. <c>A = 5 (0101)</c>.</summary>
    [ObservableProperty]
    private string _headerText = "";

    /// <summary>The editable decimal quick-set text; canonicalized to the applied value.</summary>
    [ObservableProperty]
    private string _valueText = "0";

    /// <summary>The bus's current decimal value, derived from the member bits (index 0 = LSB).</summary>
    public long DecimalValue { get; private set; }

    /// <summary>Removes the member subscriptions — the bus row is being discarded.</summary>
    public void Detach()
    {
        foreach (var member in Members)
            member.PropertyChanged -= OnMemberChanged;
    }

    private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicNetworkInputViewModel.IsOn))
            RefreshFromMembers();
    }

    partial void OnValueTextChanged(string value)
    {
        if (_updatingValue)
            return;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            ApplyToMembers(Math.Clamp(parsed, 0, MaxValue));
        // Whatever was typed, the field ends on the canonical text of the applied
        // (clamped) value — or of the unchanged bits when the text was no number.
        RefreshFromMembers();
    }

    /// <summary>Re-derives the decimal value and both display texts from the member bits.</summary>
    private void RefreshFromMembers()
    {
        long value = 0;
        foreach (var (index, member) in _byIndex)
        {
            if (member.IsOn)
                value |= 1L << index;
        }
        DecimalValue = value;
        HeaderText = $"{Prefix} = {value.ToString(CultureInfo.InvariantCulture)} ({BinaryText(value)})";
        SetValueText(value);
    }

    /// <summary>Sets the member toggles to <paramref name="value"/> (index 0 = LSB).</summary>
    private void ApplyToMembers(long value)
    {
        foreach (var (index, member) in _byIndex)
            member.IsOn = ((value >> index) & 1L) != 0;
    }

    private void SetValueText(long value)
    {
        _updatingValue = true;
        ValueText = value.ToString(CultureInfo.InvariantCulture);
        _updatingValue = false;
    }

    /// <summary>The binary form of <paramref name="value"/>, most-significant bit first.</summary>
    private string BinaryText(long value)
    {
        var chars = new char[_byIndex.Length];
        for (var position = 0; position < _byIndex.Length; position++)
            chars[position] = ((value >> _byIndex[_byIndex.Length - 1 - position].Index) & 1L) != 0 ? '1' : '0';
        return new string(chars);
    }
}

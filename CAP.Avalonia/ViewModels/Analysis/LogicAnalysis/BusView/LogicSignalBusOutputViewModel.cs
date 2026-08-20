using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

/// <summary>
/// One output bus of the Logic panel's bus view (issue #1068, NAND game rung 5): the
/// header row for an indexed output family like the 4-bit adder's <c>S0</c>–<c>S3</c>,
/// showing the family's live decimal value (<c>S = 8 (1000)</c>) so the result of a
/// computation reads as a number, not four separate bits. Index 0 is the
/// least-significant bit. Output buses are read-only — only input buses take a typed
/// decimal. Display-level only: the network itself is untouched.
/// </summary>
public partial class LogicSignalBusOutputViewModel : LogicOutputRowViewModel
{
    private readonly (int Index, LogicNetworkOutputViewModel Member)[] _byIndex;

    /// <summary>Groups the <paramref name="members"/> of one indexed output family under <paramref name="prefix"/>.</summary>
    public LogicSignalBusOutputViewModel(string prefix, IEnumerable<LogicNetworkOutputViewModel> members)
    {
        Prefix = prefix;
        _byIndex = members
            .Select(m => SignalBusName.TrySplit(m.PinName, out _, out var index) ? (index, m) : (0, m))
            .OrderBy(pair => pair.Item1)
            .ToArray();
        Members = _byIndex.Select(pair => pair.Member).ToList();
        foreach (var member in Members)
            member.PropertyChanged += OnMemberChanged;
        RefreshFromMembers();
    }

    /// <summary>The shared name prefix of the family ("S" for S0–S3).</summary>
    public string Prefix { get; }

    /// <summary>The member indicators in index order (index 0 = least-significant bit first).</summary>
    public IReadOnlyList<LogicNetworkOutputViewModel> Members { get; }

    /// <summary>Header line for the bus, e.g. <c>S = 8 (1000)</c>.</summary>
    [ObservableProperty]
    private string _headerText = "";

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
        if (e.PropertyName == nameof(LogicNetworkOutputViewModel.IsOne))
            RefreshFromMembers();
    }

    /// <summary>Re-derives the decimal value and the header text from the member bits.</summary>
    private void RefreshFromMembers()
    {
        long value = 0;
        foreach (var (index, member) in _byIndex)
        {
            if (member.IsOne)
                value |= 1L << index;
        }
        DecimalValue = value;
        HeaderText = $"{Prefix} = {value.ToString(CultureInfo.InvariantCulture)} ({BinaryText(value)})";
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

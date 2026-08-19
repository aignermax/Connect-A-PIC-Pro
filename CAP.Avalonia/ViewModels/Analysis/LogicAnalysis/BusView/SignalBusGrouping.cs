namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

/// <summary>
/// Groups the Logic panel's flat signal lists into display rows (issue #1068, NAND game
/// rung 5): names sharing an indexed family (<c>A0</c>–<c>A3</c>, see
/// <see cref="SignalBusName"/>) collapse into one bus header row when at least
/// <see cref="MinBusMembers"/> members share the prefix; every other name — no trailing
/// index, a raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id, or a lone indexed name — stays a
/// plain row. Row order follows first occurrence; bus members sort by index.
/// </summary>
public static class SignalBusGrouping
{
    /// <summary>A family needs at least this many members to become a bus row.</summary>
    public const int MinBusMembers = 2;

    /// <summary>Groups the network inputs into bus-header and single-toggle rows.</summary>
    public static IReadOnlyList<LogicInputRowViewModel> GroupInputs(IReadOnlyList<LogicNetworkInputViewModel> inputs)
    {
        var rows = new List<LogicInputRowViewModel>();
        foreach (var family in FamiliesOf(inputs.Select(i => i.PinName).ToList()))
        {
            if (family.Prefix == null)
                rows.Add(inputs[family.Positions[0]]);
            else
                rows.Add(new LogicSignalBusInputViewModel(
                    family.Prefix, family.Positions.Select(p => inputs[p])));
        }
        return rows;
    }

    /// <summary>Groups the network outputs into bus-header and single-indicator rows.</summary>
    public static IReadOnlyList<LogicOutputRowViewModel> GroupOutputs(IReadOnlyList<LogicNetworkOutputViewModel> outputs)
    {
        var rows = new List<LogicOutputRowViewModel>();
        foreach (var family in FamiliesOf(outputs.Select(o => o.PinName).ToList()))
        {
            if (family.Prefix == null)
                rows.Add(outputs[family.Positions[0]]);
            else
                rows.Add(new LogicSignalBusOutputViewModel(
                    family.Prefix, family.Positions.Select(p => outputs[p])));
        }
        return rows;
    }

    /// <summary>
    /// The ordered families of <paramref name="names"/>: one entry per bus-forming
    /// prefix (member positions sorted by index) and one singleton entry per name that
    /// does not group (<see langword="null"/> prefix), each in first-occurrence order.
    /// </summary>
    private static IEnumerable<(string? Prefix, List<int> Positions)> FamiliesOf(IReadOnlyList<string> names)
    {
        var groups = new List<(string? Prefix, List<int> Positions)>();
        var byPrefix = new Dictionary<string, List<int>>();
        for (var position = 0; position < names.Count; position++)
        {
            if (!SignalBusName.TrySplit(names[position], out var prefix, out _))
            {
                groups.Add((null, new List<int> { position }));
                continue;
            }
            if (!byPrefix.TryGetValue(prefix, out var positions))
            {
                positions = new List<int>();
                byPrefix[prefix] = positions;
                groups.Add((prefix, positions));
            }
            positions.Add(position);
        }
        foreach (var group in groups)
        {
            if (group.Positions.Count >= MinBusMembers)
                yield return (group.Prefix, SortByIndex(group.Positions, names));
            else
                foreach (var position in group.Positions)
                    yield return (null, new List<int> { position });
        }
    }

    /// <summary>The member positions of one family, ordered by their parsed index.</summary>
    private static List<int> SortByIndex(List<int> positions, IReadOnlyList<string> names) =>
        positions
            .OrderBy(p => SignalBusName.TrySplit(names[p], out _, out var index) ? index : 0)
            .ToList();
}

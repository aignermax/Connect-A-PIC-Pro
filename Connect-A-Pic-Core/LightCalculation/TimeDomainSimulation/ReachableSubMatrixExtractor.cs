using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Restricts a merged single-hop S-matrix to the subgraph of pins reachable from a set
/// of source pins (field round 4, finding [4]). Light injected at the sources can only
/// ever reach pins connected by a chain of non-zero transfers, so the transitive closure
/// of the reachable sub-matrix is exactly the closure of the full matrix for every
/// (source → reachable) pair — unrelated components neither cost closure time nor can
/// they influence the result.
/// </summary>
public static class ReachableSubMatrixExtractor
{
    /// <summary>
    /// Builds the sub-matrix spanning the pins reachable from
    /// <paramref name="sourcePinIds"/> (including the sources themselves).
    /// </summary>
    /// <param name="sMatrix">Merged single-hop system S-matrix.</param>
    /// <param name="sourcePinIds">Pins where light is injected (active inputs).</param>
    public static SMatrix ExtractReachable(SMatrix sMatrix, IReadOnlyCollection<Guid> sourcePinIds)
    {
        var sources = sourcePinIds
            .Where(sMatrix.PinReference.ContainsKey)
            .Select(id => sMatrix.PinReference[id])
            .ToList();
        if (sources.Count == 0)
            return new SMatrix(new List<Guid>(), new());

        var adjacency = BuildAdjacency(sMatrix.SMat);
        var reachable = BreadthFirstSearch(adjacency, sources);

        var reverse = sMatrix.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        var reachablePinIds = reachable.OrderBy(i => i).Select(i => reverse[i]).ToList();

        var transfers = new Dictionary<(Guid, Guid), Complex>();
        foreach (var (row, col, value) in sMatrix.SMat.EnumerateIndexed(Zeros.AllowSkip))
        {
            if (value != Complex.Zero && reachable.Contains(col) && reachable.Contains(row))
                transfers[(reverse[col], reverse[row])] = value;
        }

        var sub = new SMatrix(reachablePinIds, new());
        sub.SetValues(transfers);
        return sub;
    }

    /// <summary>Adjacency lists over non-zero transfers: SMat[out, in] ≠ 0 is the edge in → out.</summary>
    private static Dictionary<int, List<int>> BuildAdjacency(Matrix<Complex> sMat)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (row, col, value) in sMat.EnumerateIndexed(Zeros.AllowSkip))
        {
            if (value == Complex.Zero)
                continue;
            if (!adjacency.TryGetValue(col, out var targets))
                adjacency[col] = targets = new List<int>();
            targets.Add(row);
        }
        return adjacency;
    }

    private static HashSet<int> BreadthFirstSearch(
        Dictionary<int, List<int>> adjacency, IReadOnlyList<int> sources)
    {
        var visited = new HashSet<int>(sources);
        var queue = new Queue<int>(sources);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var targets))
                continue;
            foreach (var next in targets)
            {
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }
        return visited;
    }
}

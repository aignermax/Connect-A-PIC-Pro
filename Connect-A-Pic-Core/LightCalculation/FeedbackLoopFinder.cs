using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Finds one feedback loop (directed cycle) in the non-zero transfer graph of a
/// single-hop S-matrix and renders the component names along it, so a singular
/// closure can tell the user WHICH loop has no steady state (field round 4).
/// </summary>
public static class FeedbackLoopFinder
{
    /// <summary>
    /// Returns the component names along one directed cycle of the non-zero transfers,
    /// in traversal order with consecutive duplicates collapsed. Empty when the graph
    /// is acyclic or no pin of the cycle has a known owner.
    /// </summary>
    /// <param name="singleHop">Single-hop matrix; SMat[out, in] ≠ 0 is the edge in → out.</param>
    /// <param name="pinOwnerNames">Pin flow id → owning component display name.</param>
    public static IReadOnlyList<string> FindLoopComponentNames(
        SMatrix singleHop, IReadOnlyDictionary<Guid, string>? pinOwnerNames)
    {
        var cycle = FindCycle(singleHop.SMat);
        if (cycle.Count == 0 || pinOwnerNames == null)
            return Array.Empty<string>();

        var reverse = singleHop.PinReference.ToDictionary(kv => kv.Value, kv => kv.Key);
        var names = new List<string>();
        foreach (var index in cycle)
        {
            if (!pinOwnerNames.TryGetValue(reverse[index], out var name))
                continue;
            if (names.Count == 0 || names[^1] != name)
                names.Add(name);
        }
        // The cycle wraps around: drop a duplicated first/last entry ("A ↔ B ↔ A").
        if (names.Count > 1 && names[0] == names[^1])
            names.RemoveAt(names.Count - 1);
        return names;
    }

    /// <summary>Renders loop names as "A ↔ B ↔ C" for embedding into messages.</summary>
    /// <param name="loopComponentNames">Names from <see cref="FindLoopComponentNames"/>.</param>
    public static string Describe(IReadOnlyList<string> loopComponentNames) =>
        string.Join(" ↔ ", loopComponentNames);

    /// <summary>
    /// Iterative depth-first search over the non-zero adjacency; returns the pin indices
    /// of the first directed cycle found (in cycle order), or an empty list when acyclic.
    /// </summary>
    private static List<int> FindCycle(Matrix<Complex> sMat)
    {
        var adjacency = BuildAdjacency(sMat);
        var state = new Dictionary<int, VisitState>();
        var parent = new Dictionary<int, int>();

        foreach (var start in adjacency.Keys)
        {
            if (state.TryGetValue(start, out var s) && s == VisitState.Done)
                continue;
            var cycle = DepthFirstSearch(start, adjacency, state, parent);
            if (cycle.Count > 0)
                return cycle;
        }
        return new List<int>();
    }

    private enum VisitState { OnStack, Done }

    private static List<int> DepthFirstSearch(
        int start,
        Dictionary<int, List<int>> adjacency,
        Dictionary<int, VisitState> state,
        Dictionary<int, int> parent)
    {
        var stack = new Stack<(int Node, int NextChild)>();
        stack.Push((start, 0));
        state[start] = VisitState.OnStack;

        while (stack.Count > 0)
        {
            var (node, childIndex) = stack.Pop();
            var children = adjacency.TryGetValue(node, out var list) ? list : null;
            if (children == null || childIndex >= children.Count)
            {
                state[node] = VisitState.Done;
                continue;
            }

            stack.Push((node, childIndex + 1));
            int next = children[childIndex];
            if (!state.TryGetValue(next, out var nextState))
            {
                parent[next] = node;
                state[next] = VisitState.OnStack;
                stack.Push((next, 0));
            }
            else if (nextState == VisitState.OnStack)
            {
                return ExtractCycle(next, node, parent);
            }
        }
        return new List<int>();
    }

    /// <summary>Walks parents from the back-edge source up to the cycle entry.</summary>
    private static List<int> ExtractCycle(int cycleStart, int backEdgeSource, Dictionary<int, int> parent)
    {
        var cycle = new List<int> { cycleStart };
        int current = backEdgeSource;
        while (current != cycleStart)
        {
            cycle.Add(current);
            if (!parent.TryGetValue(current, out current))
                break;
        }
        cycle.Reverse();
        return cycle;
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
}

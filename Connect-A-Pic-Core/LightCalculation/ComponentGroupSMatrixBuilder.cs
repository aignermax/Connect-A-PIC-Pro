using System.Numerics;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using MathNet.Numerics.LinearAlgebra;

namespace CAP_Core.LightCalculation;

/// <summary>
/// Builds S-Matrix representations for ComponentGroup instances by combining
/// child component S-Matrices and frozen internal waveguide paths.
/// </summary>
public class ComponentGroupSMatrixBuilder
{
    /// <summary>
    /// Computes the S-Matrix for a ComponentGroup at a specific wavelength.
    /// The resulting matrix maps external GroupPins to each other via internal components and paths.
    /// </summary>
    /// <param name="group">The ComponentGroup to compute S-Matrix for.</param>
    /// <param name="wavelengthNm">Wavelength in nanometers.</param>
    /// <returns>S-Matrix with GroupPin connections, or null if the group has no external pins.</returns>
    public Dictionary<int, SMatrix>? BuildGroupSMatrix(ComponentGroup group, int wavelengthNm)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        if (group.ExternalPins.Count == 0)
            return null;

        // Get all wavelengths that child components support
        var supportedWavelengths = GetSupportedWavelengths(group);

        if (supportedWavelengths.Count == 0)
            return null;

        var result = new Dictionary<int, SMatrix>();

        // Build S-Matrix for the requested wavelength
        var matrix = BuildSMatrixForWavelength(group, wavelengthNm);
        if (matrix != null)
        {
            result[wavelengthNm] = matrix;
        }

        return result;
    }

    /// <summary>
    /// Builds S-Matrices for all wavelengths supported by child components.
    /// </summary>
    public Dictionary<int, SMatrix>? BuildGroupSMatrixAllWavelengths(ComponentGroup group)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        if (group.ExternalPins.Count == 0)
            return null;

        var supportedWavelengths = GetSupportedWavelengths(group);

        if (supportedWavelengths.Count == 0)
            return null;

        var result = new Dictionary<int, SMatrix>();

        foreach (var wavelength in supportedWavelengths)
        {
            var matrix = BuildSMatrixForWavelength(group, wavelength);
            if (matrix != null)
            {
                result[wavelength] = matrix;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the full internal system matrix for a ComponentGroup at a specific wavelength.
    /// Unlike <see cref="BuildGroupSMatrixAllWavelengths"/>, this retains ALL internal child
    /// pin IDs — not just external-facing ones. Use this to propagate boundary conditions
    /// from known external pin amplitudes to compute internal field values.
    /// Returns null if no child pin IDs or matrices are available.
    /// </summary>
    /// <param name="group">The ComponentGroup to build the internal matrix for.</param>
    /// <param name="wavelengthNm">Wavelength in nanometers.</param>
    public SMatrix? BuildFullInternalMatrix(ComponentGroup group, int wavelengthNm)
    {
        if (group == null)
            throw new ArgumentNullException(nameof(group));

        return BuildFullTransitiveMatrix(group, wavelengthNm);
    }

    /// <summary>
    /// Gets all wavelengths supported by child components in the group.
    /// </summary>
    public HashSet<int> GetSupportedWavelengths(ComponentGroup group)
    {
        var wavelengths = new HashSet<int>();

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                // Recursively get wavelengths from nested groups
                foreach (var wl in GetSupportedWavelengths(childGroup))
                {
                    wavelengths.Add(wl);
                }
            }
            else
            {
                foreach (var wl in child.WaveLengthToSMatrixMap.Keys)
                {
                    wavelengths.Add(wl);
                }
            }
        }

        return wavelengths;
    }

    /// <summary>
    /// Collects all physical pin IDs from child components (both InFlow and OutFlow).
    /// For nested groups, uses their external pins; for regular components, uses all physical pins.
    /// </summary>
    private static List<Guid> CollectAllChildPinIds(ComponentGroup group)
    {
        var allChildPinIds = new List<Guid>();

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup childGroup)
            {
                // For nested groups, use their external pins
                foreach (var groupPin in childGroup.ExternalPins)
                {
                    if (groupPin.InternalPin?.LogicalPin != null)
                    {
                        allChildPinIds.Add(groupPin.InternalPin.LogicalPin.IDInFlow);
                        allChildPinIds.Add(groupPin.InternalPin.LogicalPin.IDOutFlow);
                    }
                }
            }
            else
            {
                // For regular components, use physical pins
                foreach (var pin in child.PhysicalPins)
                {
                    if (pin.LogicalPin != null)
                    {
                        allChildPinIds.Add(pin.LogicalPin.IDInFlow);
                        allChildPinIds.Add(pin.LogicalPin.IDOutFlow);
                    }
                }
            }
        }

        return allChildPinIds;
    }

    /// <summary>
    /// Collects child S-Matrices at the specified wavelength.
    /// </summary>
    private List<SMatrix> CollectChildMatrices(ComponentGroup group, int wavelengthNm)
    {
        var childMatrices = new List<SMatrix>();

        foreach (var child in group.ChildComponents)
        {
            SMatrix? childMatrix = GetChildSMatrix(child, wavelengthNm);
            if (childMatrix != null)
            {
                childMatrices.Add(childMatrix);
            }
        }

        return childMatrices;
    }

    /// <summary>
    /// Builds the full transitive matrix with ALL internal pins retained.
    /// This is the common implementation used by both the external-pin-projection path
    /// and the full-internal-matrix path.
    /// </summary>
    private SMatrix? BuildFullTransitiveMatrix(ComponentGroup group, int wavelengthNm)
    {
        var allChildPinIds = CollectAllChildPinIds(group);

        if (allChildPinIds.Count == 0)
            return null;

        var childMatrices = CollectChildMatrices(group, wavelengthNm);

        // Add connections from frozen internal paths
        var internalConnections = BuildInternalConnectionMatrix(group, allChildPinIds);
        if (internalConnections != null)
        {
            childMatrices.Add(internalConnections);
        }

        if (childMatrices.Count == 0)
            return null;

        // Combine all matrices into a system matrix
        var mergedMatrix = SMatrix.CreateSystemSMatrix(childMatrices);

        // Compute transitive closure so light propagates through multi-hop chains.
        return ComputeTransitiveMatrix(mergedMatrix);
    }

    /// <summary>
    /// Builds the S-Matrix for a specific wavelength (projected to external pins only).
    /// </summary>
    private SMatrix? BuildSMatrixForWavelength(ComponentGroup group, int wavelengthNm)
    {
        var fullMatrix = BuildFullTransitiveMatrix(group, wavelengthNm);
        if (fullMatrix == null)
            return null;

        // Create the external pin mapping
        var externalPinIds = new List<Guid>();
        foreach (var extPin in group.ExternalPins)
        {
            if (extPin.InternalPin?.LogicalPin != null)
            {
                externalPinIds.Add(extPin.InternalPin.LogicalPin.IDInFlow);
                externalPinIds.Add(extPin.InternalPin.LogicalPin.IDOutFlow);
            }
        }

        // Extract the sub-matrix for external pins only
        return ExtractExternalPinMatrix(fullMatrix, externalPinIds);
    }

    /// <summary>
    /// Gets the S-Matrix for a child component at the specified wavelength.
    /// </summary>
    private SMatrix? GetChildSMatrix(Component child, int wavelengthNm)
    {
        if (child is ComponentGroup childGroup)
        {
            // Recursively build S-Matrix for nested groups
            var childGroupMatrices = BuildGroupSMatrixAllWavelengths(childGroup);
            if (childGroupMatrices != null && childGroupMatrices.TryGetValue(wavelengthNm, out var matrix))
            {
                return matrix;
            }

            // Try nearest wavelength fallback
            if (childGroupMatrices != null && childGroupMatrices.Count > 0)
            {
                var nearestWl = childGroupMatrices.Keys
                    .OrderBy(k => Math.Abs(k - wavelengthNm))
                    .First();
                return childGroupMatrices[nearestWl];
            }

            return null;
        }

        if (child.WaveLengthToSMatrixMap.TryGetValue(wavelengthNm, out var childMatrix))
        {
            return childMatrix;
        }

        // Fallback to nearest wavelength
        if (child.WaveLengthToSMatrixMap.Count > 0)
        {
            var nearestKey = child.WaveLengthToSMatrixMap.Keys
                .OrderBy(k => Math.Abs(k - wavelengthNm))
                .First();
            return child.WaveLengthToSMatrixMap[nearestKey];
        }

        return null;
    }

    /// <summary>
    /// Builds a connection matrix for frozen internal waveguide paths.
    /// </summary>
    private SMatrix? BuildInternalConnectionMatrix(ComponentGroup group, List<Guid> allPinIds)
    {
        if (group.InternalPaths.Count == 0)
            return null;

        var connections = new Dictionary<(Guid, Guid), Complex>();

        foreach (var frozenPath in group.InternalPaths)
        {
            // Skip paths where pins don't have LogicalPins (shouldn't happen in valid groups, but be defensive)
            if (frozenPath.StartPin?.LogicalPin == null || frozenPath.EndPin?.LogicalPin == null)
                continue;

            var startOutFlow = frozenPath.StartPin.LogicalPin.IDOutFlow;
            var startInFlow = frozenPath.StartPin.LogicalPin.IDInFlow;
            var endOutFlow = frozenPath.EndPin.LogicalPin.IDOutFlow;
            var endInFlow = frozenPath.EndPin.LogicalPin.IDInFlow;

            var transmission = frozenPath.TransmissionCoefficient;

            // Forward: light exits StartPin (OutFlow) and enters EndPin (InFlow)
            connections[(startOutFlow, endInFlow)] = transmission;
            // Reverse: light exits EndPin (OutFlow) and enters StartPin (InFlow)
            connections[(endOutFlow, startInFlow)] = transmission;
        }

        if (connections.Count == 0)
            return null;

        var connectionMatrix = new SMatrix(allPinIds, new());
        connectionMatrix.SetValues(connections);

        return connectionMatrix;
    }

    /// <summary>
    /// Computes the transitive S-Matrix via the Neumann series (M + M² + … + Mᵏ).
    /// This is required because CreateSystemSMatrix only stores single-hop transfers.
    /// Delegates to the shared <see cref="TransitiveSMatrixCalculator"/> (also used by
    /// the transient impulse-response extraction) so group and flat circuits share one
    /// multi-hop physics implementation. The series iterates to residual-based
    /// convergence; a resonant group topology (or non-physical member matrix) aborts
    /// with <see cref="NonConvergentCircuitException"/> instead of silently returning a
    /// truncated partial sum — the pre-round-4 pin-count cutoff hid exactly that error.
    /// </summary>
    /// <param name="singleHopMatrix">Merged single-hop S-Matrix for the group.</param>
    private static SMatrix ComputeTransitiveMatrix(SMatrix singleHopMatrix)
        => TransitiveSMatrixCalculator.Compute(singleHopMatrix);

    /// <summary>
    /// Extracts a sub-matrix containing only the specified external pins.
    /// This reduces the full system matrix to just the group's external interface.
    /// </summary>
    private SMatrix ExtractExternalPinMatrix(SMatrix systemMatrix, List<Guid> externalPinIds)
    {
        var externalMatrix = new SMatrix(externalPinIds, new());
        var transfers = new Dictionary<(Guid, Guid), Complex>();

        // Extract only the rows/columns for external pins
        foreach (var pinIn in externalPinIds)
        {
            foreach (var pinOut in externalPinIds)
            {
                if (pinIn == pinOut)
                    continue;

                if (systemMatrix.PinReference.TryGetValue(pinIn, out int idxIn) &&
                    systemMatrix.PinReference.TryGetValue(pinOut, out int idxOut))
                {
                    var value = systemMatrix.SMat[idxOut, idxIn];
                    if (value != Complex.Zero)
                    {
                        transfers[(pinIn, pinOut)] = value;
                    }
                }
            }
        }

        externalMatrix.SetValues(transfers);
        return externalMatrix;
    }
}

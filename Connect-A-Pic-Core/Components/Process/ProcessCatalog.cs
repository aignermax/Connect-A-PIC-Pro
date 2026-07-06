using System.Collections.Generic;
using System.Linq;

namespace CAP_Core.Components.Process;

/// <summary>
/// Groups the currently loaded PDKs into processes by compatibility (issue #570). PDKs with
/// compatible fingerprints collapse into one group; unspecified PDKs each stay a singleton.
/// No persistence — the catalog is derived fresh from whatever PDKs are loaded.
/// </summary>
public static class ProcessCatalog
{
    /// <summary>Builds the process groups from the given PDK fingerprints.</summary>
    public static IReadOnlyList<ProcessGroup> BuildGroups(IEnumerable<PdkProcessEntry> pdks)
    {
        var groups = new List<List<PdkProcessEntry>>();

        foreach (var entry in pdks)
        {
            var target = entry.Fingerprint.IsSpecified
                ? groups.FirstOrDefault(g =>
                    g[0].Fingerprint.IsSpecified &&
                    g.All(m => ProcessCompatibility.AreCompatible(m.Fingerprint, entry.Fingerprint)))
                : null;   // unspecified never joins an existing group

            if (target == null)
                groups.Add(new List<PdkProcessEntry> { entry });
            else
                target.Add(entry);
        }

        return groups.Select(ToGroup).ToList();
    }

    private static ProcessGroup ToGroup(List<PdkProcessEntry> members)
    {
        var fp = members[0].Fingerprint;
        return new ProcessGroup(DeriveDisplayName(members), fp, members.Select(m => m.PdkName).ToList());
    }

    private static string DeriveDisplayName(List<PdkProcessEntry> members)
    {
        var fp = members[0].Fingerprint;
        if (!string.IsNullOrWhiteSpace(fp.ProcessName))
            return fp.ProcessName!;
        if (fp.IsSpecified)
            return $"{fp.CoreMaterial} {fp.CoreThicknessNm:0} nm · {fp.Cladding} · {fp.DesignWavelengthNm} nm";
        return members[0].PdkName;   // unspecified singleton
    }
}

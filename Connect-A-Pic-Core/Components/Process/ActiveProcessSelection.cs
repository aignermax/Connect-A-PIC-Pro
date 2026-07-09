using System.Collections.Generic;
using System.Linq;

namespace CAP_Core.Components.Process;

/// <summary>
/// The process a design is currently locked to (issue #570): a real process (with its member
/// PDKs), or Playground (mixing allowed, not manufacturable). Persisted with the design.
/// </summary>
public sealed record ActiveProcessSelection(
    string DisplayName,
    ProcessFingerprint? Fingerprint,
    IReadOnlyList<string> MemberPdkNames,
    bool IsPlayground)
{
    /// <summary>The sandbox selection: any component allowed, chip not manufacturable.</summary>
    public static ActiveProcessSelection Playground() =>
        new("Playground", Fingerprint: null, MemberPdkNames: new List<string>(), IsPlayground: true);

    /// <summary>Locks to a derived process group.</summary>
    public static ActiveProcessSelection ForGroup(ProcessGroup group) =>
        new(group.DisplayName, group.Fingerprint, group.MemberPdkNames.ToList(), IsPlayground: false);
}

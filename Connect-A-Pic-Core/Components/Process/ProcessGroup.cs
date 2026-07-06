using System.Collections.Generic;

namespace CAP_Core.Components.Process;

/// <summary>One PDK paired with the process fingerprint extracted from it (issue #570).</summary>
/// <param name="PdkName">Name of the loaded PDK.</param>
/// <param name="Fingerprint">The process fingerprint extracted from that PDK.</param>
public sealed record PdkProcessEntry(string PdkName, ProcessFingerprint Fingerprint);

/// <summary>
/// A selectable fabrication process: the set of loaded PDKs whose fingerprints are mutually
/// compatible, so their components may share one chip (issue #570).
/// </summary>
/// <param name="DisplayName">Human-readable label for the group, e.g. a shared process name or a synthesized description.</param>
/// <param name="Fingerprint">The representative fingerprint for the group (taken from its first member).</param>
/// <param name="MemberPdkNames">Names of all PDKs belonging to this group.</param>
public sealed record ProcessGroup(
    string DisplayName,
    ProcessFingerprint Fingerprint,
    IReadOnlyList<string> MemberPdkNames);

using System.Collections.Generic;

namespace CAP_Core.Components.Process;

/// <summary>One PDK paired with the process fingerprint extracted from it (issue #570).</summary>
public sealed record PdkProcessEntry(string PdkName, ProcessFingerprint Fingerprint);

/// <summary>
/// A selectable fabrication process: the set of loaded PDKs whose fingerprints are mutually
/// compatible, so their components may share one chip (issue #570).
/// </summary>
public sealed record ProcessGroup(
    string DisplayName,
    ProcessFingerprint Fingerprint,
    IReadOnlyList<string> MemberPdkNames);

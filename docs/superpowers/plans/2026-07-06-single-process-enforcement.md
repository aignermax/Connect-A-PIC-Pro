# Single-Process Enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lock every design to exactly one fabrication process (physically compatible PDKs), chosen consciously at New-Design time, with a persistent indicator, a process-filtered component library, and placement/paste enforcement.

**Architecture:** Pure process logic (fingerprint, compatibility, grouping, placement policy) lives in `CAP-Core` operating on primitives; extraction from the `PdkDraft` DTO lives in `CAP-DataAccess` (which references Core). ViewModels hold the active-process selection, persist it in the `.lun`, drive the library filter, and enforce at the two placement paths. Supersedes PR #602 (PDK-name lock).

**Tech Stack:** C# / .NET 10, Avalonia 11 / CommunityToolkit.Mvvm, xUnit + Shouldly + Moq. Spec: `docs/superpowers/specs/2026-07-06-single-process-enforcement-design.md`.

## Global Constraints

- Tests run via `py "$env:USERPROFILE\.cap-tools\smart_test.py" <Pattern>` with `$env:PYTHONUTF8='1'` (NOT `dotnet test` directly). Build: `dotnet build ConnectAPICPro.sln`.
- Max 250 lines per NEW file; hard cap 500 lines/file (enforced by `FileSizeLimitTests`).
- `CAP-Core` must NOT reference `CAP-DataAccess`. Pure logic → Core; DTO-touching code → DataAccess.
- Every public class/method gets XML docs. `_camelCase` private fields, PascalCase public.
- No magic numbers — tolerances are named constants.
- Machine-facing/number formatting uses `CultureInfo.InvariantCulture`.
- Built-in/tool exemption: a component whose PDK source is `null`, `""`, or `"Built-in"` is always allowed.
- Compatibility tolerances: **core thickness ±5 nm**, **design wavelength ±40 nm**; core material + cladding are exact (case-insensitive).

---

## Phase 1 — Core process logic (pure, no UI)

### Task 1: `ProcessFingerprint` + `ProcessCompatibility`

**Files:**
- Create: `Connect-A-Pic-Core/Components/Process/ProcessFingerprint.cs`
- Create: `Connect-A-Pic-Core/Components/Process/ProcessCompatibility.cs`
- Test: `UnitTests/Components/Process/ProcessCompatibilityTests.cs`

**Interfaces:**
- Produces: `record ProcessFingerprint(string? CoreMaterial, double? CoreThicknessNm, string? Cladding, int DesignWavelengthNm, string? ProcessName)` with `bool IsSpecified`.
- Produces: `static bool ProcessCompatibility.AreCompatible(ProcessFingerprint a, ProcessFingerprint b)`, `const double CoreThicknessToleranceNm = 5`, `const int WavelengthToleranceNm = 40`.

- [ ] **Step 1: Write the failing test**

```csharp
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessCompatibilityTests
{
    private static ProcessFingerprint Fp(string? mat, double? thick, string? clad, int wl) =>
        new(mat, thick, clad, wl, ProcessName: null);

    [Fact]
    public void SameMaterialWithinTolerances_IsCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("si", 222, "sio2", 1560)).ShouldBeTrue();
    }

    [Fact]
    public void DifferentCoreMaterial_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("SiN", 220, "SiO2", 1550)).ShouldBeFalse();
    }

    [Fact]
    public void ThicknessBeyondTolerance_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("Si", 340, "SiO2", 1550)).ShouldBeFalse();
    }

    [Fact]
    public void WavelengthBeyondTolerance_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("Si", 220, "SiO2", 1310)).ShouldBeFalse();
    }

    [Fact]
    public void UnspecifiedFingerprint_IsNeverCompatible()
    {
        var unspecified = Fp(null, null, null, 1550);
        ProcessCompatibility.AreCompatible(unspecified, unspecified).ShouldBeFalse();
        unspecified.IsSpecified.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ProcessCompatibility`
Expected: FAIL — types `ProcessFingerprint` / `ProcessCompatibility` do not exist.

- [ ] **Step 3: Implement `ProcessFingerprint`**

```csharp
namespace CAP_Core.Components.Process;

/// <summary>
/// The physical identity of a fabrication process, derived from a PDK (issue #570).
/// Two PDKs are usable on the same chip when their fingerprints are compatible.
/// </summary>
/// <param name="CoreMaterial">Waveguide core material name (e.g. "Si", "SiN"); null if unspecified.</param>
/// <param name="CoreThicknessNm">Core layer thickness in nm; null if unspecified.</param>
/// <param name="Cladding">Cladding material name (e.g. "SiO2"); null if unspecified.</param>
/// <param name="DesignWavelengthNm">Representative design wavelength in nm.</param>
/// <param name="ProcessName">Human-readable process label for display; not used for matching.</param>
public sealed record ProcessFingerprint(
    string? CoreMaterial,
    double? CoreThicknessNm,
    string? Cladding,
    int DesignWavelengthNm,
    string? ProcessName)
{
    /// <summary>True when the fingerprint carries enough physical data to group by (core material present).</summary>
    public bool IsSpecified => !string.IsNullOrWhiteSpace(CoreMaterial);
}
```

- [ ] **Step 4: Implement `ProcessCompatibility`**

```csharp
using System;

namespace CAP_Core.Components.Process;

/// <summary>
/// Decides whether two <see cref="ProcessFingerprint"/>s describe the same fabrication
/// process (issue #570). Core material and cladding must match exactly (case-insensitive);
/// core thickness and design wavelength must fall within a small tolerance.
/// </summary>
public static class ProcessCompatibility
{
    /// <summary>Max core-thickness difference (nm) still considered the same process.</summary>
    public const double CoreThicknessToleranceNm = 5;

    /// <summary>Max design-wavelength difference (nm) still considered the same process.</summary>
    public const int WavelengthToleranceNm = 40;

    /// <summary>Returns true when both fingerprints are specified and physically compatible.</summary>
    public static bool AreCompatible(ProcessFingerprint a, ProcessFingerprint b)
    {
        if (!a.IsSpecified || !b.IsSpecified)
            return false;

        if (!string.Equals(a.CoreMaterial, b.CoreMaterial, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(a.Cladding, b.Cladding, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Math.Abs((a.CoreThicknessNm ?? 0) - (b.CoreThicknessNm ?? 0)) > CoreThicknessToleranceNm)
            return false;

        return Math.Abs(a.DesignWavelengthNm - b.DesignWavelengthNm) <= WavelengthToleranceNm;
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ProcessCompatibility`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add Connect-A-Pic-Core/Components/Process/ProcessFingerprint.cs Connect-A-Pic-Core/Components/Process/ProcessCompatibility.cs UnitTests/Components/Process/ProcessCompatibilityTests.cs
git commit -m "(+) Single-process: ProcessFingerprint + compatibility rule (#570)"
```

---

### Task 2: `ProcessCatalog` — group compatible PDKs

**Files:**
- Create: `Connect-A-Pic-Core/Components/Process/ProcessGroup.cs`
- Create: `Connect-A-Pic-Core/Components/Process/ProcessCatalog.cs`
- Test: `UnitTests/Components/Process/ProcessCatalogTests.cs`

**Interfaces:**
- Consumes: `ProcessFingerprint`, `ProcessCompatibility.AreCompatible`.
- Produces: `record PdkProcessEntry(string PdkName, ProcessFingerprint Fingerprint)`.
- Produces: `record ProcessGroup(string DisplayName, ProcessFingerprint Fingerprint, IReadOnlyList<string> MemberPdkNames)`.
- Produces: `static IReadOnlyList<ProcessGroup> ProcessCatalog.BuildGroups(IEnumerable<PdkProcessEntry> pdks)`.

- [ ] **Step 1: Write the failing test**

```csharp
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessCatalogTests
{
    private static PdkProcessEntry Pdk(string name, string? mat, double? thick, string? clad, int wl, string? proc = null) =>
        new(name, new ProcessFingerprint(mat, thick, clad, wl, proc));

    [Fact]
    public void CompatiblePdks_CollapseIntoOneGroup()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("Foundry SOI", "Si", 220, "SiO2", 1550, "SOI 220"),
            Pdk("Custom SOI",  "Si", 221, "SiO2", 1555),
        });

        groups.Count.ShouldBe(1);
        groups[0].MemberPdkNames.ShouldBe(new[] { "Foundry SOI", "Custom SOI" }, ignoreOrder: true);
    }

    [Fact]
    public void IncompatiblePdks_FormSeparateGroups()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("SOI",  "Si",  220, "SiO2", 1550),
            Pdk("SiNx", "SiN", 340, "SiO2", 1550),
        });
        groups.Count.ShouldBe(2);
    }

    [Fact]
    public void UnspecifiedPdk_IsItsOwnSingletonGroup()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("Legacy A", null, null, null, 1550),
            Pdk("Legacy B", null, null, null, 1550),
        });
        groups.Count.ShouldBe(2);   // never merge unspecified fingerprints
    }

    [Fact]
    public void GroupDisplayName_PrefersSharedProcessName()
    {
        var groups = ProcessCatalog.BuildGroups(new[] { Pdk("P", "Si", 220, "SiO2", 1550, "AMF SOI 220nm") });
        groups[0].DisplayName.ShouldBe("AMF SOI 220nm");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ProcessCatalog`
Expected: FAIL — `ProcessCatalog` / `ProcessGroup` / `PdkProcessEntry` missing.

- [ ] **Step 3: Implement `ProcessGroup` + `PdkProcessEntry`**

```csharp
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
```

- [ ] **Step 4: Implement `ProcessCatalog`**

```csharp
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
                    ProcessCompatibility.AreCompatible(g[0].Fingerprint, entry.Fingerprint))
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
```

- [ ] **Step 5: Run to verify pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ProcessCatalog`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add Connect-A-Pic-Core/Components/Process/ProcessGroup.cs Connect-A-Pic-Core/Components/Process/ProcessCatalog.cs UnitTests/Components/Process/ProcessCatalogTests.cs
git commit -m "(+) Single-process: ProcessCatalog groups compatible PDKs (#570)"
```

---

### Task 3: `ActiveProcessSelection` + `SingleProcessPolicy`

**Files:**
- Create: `Connect-A-Pic-Core/Components/Process/ActiveProcessSelection.cs`
- Create: `Connect-A-Pic-Core/Components/Process/SingleProcessPolicy.cs`
- Test: `UnitTests/Components/Process/SingleProcessPolicyTests.cs`

**Interfaces:**
- Consumes: `ProcessGroup`, `ProcessFingerprint`.
- Produces: `record ActiveProcessSelection` with `string DisplayName`, `ProcessFingerprint? Fingerprint`, `IReadOnlyList<string> MemberPdkNames`, `bool IsPlayground`; statics `Playground()` and `ForGroup(ProcessGroup)`.
- Produces: `static (bool IsAllowed, string? BlockReason) SingleProcessPolicy.CheckPlacement(ActiveProcessSelection? active, string? componentPdkName)`.
- Produces: `static bool SingleProcessPolicy.IsBuiltIn(string? pdkSource)`.

- [ ] **Step 1: Write the failing test**

```csharp
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class SingleProcessPolicyTests
{
    private static ActiveProcessSelection Soi() => ActiveProcessSelection.ForGroup(
        new ProcessGroup("SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            new[] { "Foundry SOI", "Custom SOI" }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Built-in")]
    public void BuiltInComponent_IsAlwaysAllowed(string? pdk)
    {
        SingleProcessPolicy.CheckPlacement(Soi(), pdk).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void MemberPdk_IsAllowed()
    {
        SingleProcessPolicy.CheckPlacement(Soi(), "Custom SOI").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ForeignPdk_IsBlockedWithReason()
    {
        var (ok, reason) = SingleProcessPolicy.CheckPlacement(Soi(), "HHI-InP");
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("HHI-InP");
        reason.ShouldContain("SOI 220");
    }

    [Fact]
    public void Playground_AllowsAnything()
    {
        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), "HHI-InP").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void NoActiveProcess_AllowsAnything()
    {
        SingleProcessPolicy.CheckPlacement(null, "HHI-InP").IsAllowed.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" SingleProcessPolicy`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement `ActiveProcessSelection`**

```csharp
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
```

- [ ] **Step 4: Implement `SingleProcessPolicy`**

```csharp
using System;
using System.Linq;

namespace CAP_Core.Components.Process;

/// <summary>
/// Enforces the single-process-per-design rule at component placement (issue #570).
/// Process-keyed successor to the PDK-name-based policy from PR #602.
/// </summary>
public static class SingleProcessPolicy
{
    /// <summary>The reserved PDK-source label for process-agnostic built-in/tool components.</summary>
    public const string BuiltInSource = "Built-in";

    /// <summary>True when the PDK source denotes a built-in / tool (process-agnostic) component.</summary>
    public static bool IsBuiltIn(string? pdkSource) =>
        string.IsNullOrWhiteSpace(pdkSource) ||
        string.Equals(pdkSource, BuiltInSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a component from <paramref name="componentPdkName"/> may be placed on a
    /// design locked to <paramref name="active"/>. Built-ins, Playground, and an unset selection
    /// always pass; otherwise the component's PDK must be a member of the active process.
    /// </summary>
    public static (bool IsAllowed, string? BlockReason) CheckPlacement(
        ActiveProcessSelection? active, string? componentPdkName)
    {
        if (IsBuiltIn(componentPdkName))
            return (true, null);

        if (active == null || active.IsPlayground)
            return (true, null);

        if (active.MemberPdkNames.Contains(componentPdkName!, StringComparer.OrdinalIgnoreCase))
            return (true, null);

        return (false,
            $"This component belongs to '{componentPdkName}', but the chip is locked to the process " +
            $"'{active.DisplayName}'. A monolithic design uses one process — start a new design (or use " +
            "Playground) to mix processes.");
    }
}
```

- [ ] **Step 5: Run to verify pass** — `... SingleProcessPolicy` → PASS (7 cases).

- [ ] **Step 6: Commit**

```bash
git add Connect-A-Pic-Core/Components/Process/ActiveProcessSelection.cs Connect-A-Pic-Core/Components/Process/SingleProcessPolicy.cs UnitTests/Components/Process/SingleProcessPolicyTests.cs
git commit -m "(+) Single-process: ActiveProcessSelection + placement policy (#570)"
```

---

## Phase 2 — PDK metadata extraction (DataAccess)

### Task 4: `ProcessDefinition.CoreThicknessNm` + `ProcessFingerprintFactory`

**Files:**
- Modify: `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/ProcessDefinition.cs` (add `CoreThicknessNm`)
- Create: `CAP-DataAccess/Components/ComponentDraftMapper/ProcessFingerprintFactory.cs`
- Test: `UnitTests/Components/Process/ProcessFingerprintFactoryTests.cs`

**Interfaces:**
- Consumes: `PdkDraft` (has `Process`, `DefaultWavelengthNm`), `ProcessDefinition` (has `Materials` with `Role`, and the new `CoreThicknessNm`), `ProcessFingerprint`.
- Produces: `static ProcessFingerprint ProcessFingerprintFactory.From(PdkDraft draft)`.

- [ ] **Step 1: Write the failing test**

```csharp
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessFingerprintFactoryTests
{
    [Fact]
    public void From_PdkWithProcess_ExtractsMaterialsThicknessAndWavelength()
    {
        var draft = new PdkDraft
        {
            Name = "Demo", DefaultWavelengthNm = 1550,
            Process = new ProcessDefinition
            {
                Name = "SOI 220", CoreThicknessNm = 220,
                Materials =
                {
                    new ProcessMaterial { Name = "Si",   Role = "core" },
                    new ProcessMaterial { Name = "SiO2", Role = "cladding" },
                },
            },
        };

        var fp = ProcessFingerprintFactory.From(draft);

        fp.CoreMaterial.ShouldBe("Si");
        fp.Cladding.ShouldBe("SiO2");
        fp.CoreThicknessNm.ShouldBe(220);
        fp.DesignWavelengthNm.ShouldBe(1550);
        fp.ProcessName.ShouldBe("SOI 220");
        fp.IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void From_PdkWithoutProcess_IsUnspecified()
    {
        var fp = ProcessFingerprintFactory.From(new PdkDraft { Name = "Legacy", DefaultWavelengthNm = 1550 });
        fp.IsSpecified.ShouldBeFalse();
        fp.DesignWavelengthNm.ShouldBe(1550);
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `... ProcessFingerprintFactory` → FAIL (factory + `CoreThicknessNm` missing).

- [ ] **Step 3: Add `CoreThicknessNm` to `ProcessDefinition`**

In `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/ProcessDefinition.cs`, add inside `ProcessDefinition` after `Version`:

```csharp
        /// <summary>
        /// Defining waveguide-core thickness in nm (e.g. 220 for 220 nm SOI). The key
        /// physical axis for process compatibility (issue #570); optional so old PDKs parse.
        /// </summary>
        [JsonPropertyName("coreThicknessNm")]
        public double? CoreThicknessNm { get; set; }
```

- [ ] **Step 4: Implement `ProcessFingerprintFactory`**

```csharp
using System;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

/// <summary>
/// Builds a <see cref="ProcessFingerprint"/> from a loaded <see cref="PdkDraft"/> (issue #570).
/// Core/cladding come from the process materials' <c>Role</c>; wavelength from the PDK default;
/// thickness from the process. A PDK without a process block yields an unspecified fingerprint.
/// </summary>
public static class ProcessFingerprintFactory
{
    /// <summary>Extracts the process fingerprint for the given PDK.</summary>
    public static ProcessFingerprint From(PdkDraft draft)
    {
        var process = draft.Process;
        var core = MaterialByRole(process, "core");
        var cladding = MaterialByRole(process, "cladding");

        return new ProcessFingerprint(
            CoreMaterial: core,
            CoreThicknessNm: process?.CoreThicknessNm,
            Cladding: cladding,
            DesignWavelengthNm: draft.DefaultWavelengthNm,
            ProcessName: string.IsNullOrWhiteSpace(process?.Name) ? null : process!.Name);
    }

    private static string? MaterialByRole(ProcessDefinition? process, string role) =>
        process?.Materials
            .FirstOrDefault(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase))
            ?.Name;
}
```

- [ ] **Step 5: Run to verify pass** — `... ProcessFingerprintFactory` → PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add CAP-DataAccess/Components/ComponentDraftMapper/DTOs/ProcessDefinition.cs CAP-DataAccess/Components/ComponentDraftMapper/ProcessFingerprintFactory.cs UnitTests/Components/Process/ProcessFingerprintFactoryTests.cs
git commit -m "(+) Single-process: coreThicknessNm + ProcessFingerprintFactory (#570)"
```

---

### Task 5: Populate bundled PDK process blocks

**Files:**
- Modify: `CAP-DataAccess/PDKs/demo-pdk.json` (add top-level `process`)
- Modify: `CAP-DataAccess/PDKs/siepic-ebeam-pdk.json` (add top-level `process`)
- Test: `UnitTests/Components/Process/BundledPdkProcessTests.cs`

**Interfaces:**
- Consumes: `PdkLoader` (loads a PDK file → `PdkDraft`), `ProcessFingerprintFactory`, `ProcessCatalog`.

- [ ] **Step 1: Add the `process` block to `demo-pdk.json`** — insert after the `"defaultWavelengthNm": 1550,` line:

```json
  "process": {
    "name": "Demo SOI 220nm",
    "foundry": "Demo Foundry",
    "coreThicknessNm": 220,
    "materials": [
      { "name": "Si",   "role": "core" },
      { "name": "SiO2", "role": "cladding" }
    ]
  },
```

- [ ] **Step 2: Add the `process` block to `siepic-ebeam-pdk.json`** — insert after its `"defaultWavelengthNm"` line (SiEPIC EBeam is 220 nm SOI at 1550 nm — same process family as Demo on purpose, to exercise grouping):

```json
  "process": {
    "name": "SiEPIC EBeam SOI 220nm",
    "foundry": "Applied Nanotools",
    "coreThicknessNm": 220,
    "materials": [
      { "name": "Si",   "role": "core" },
      { "name": "SiO2", "role": "cladding" }
    ]
  },
```

- [ ] **Step 3: Write the test**

```csharp
using System.IO;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class BundledPdkProcessTests
{
    private static string PdkDir =>
        Path.Combine(TestPaths.RepoRoot, "CAP-DataAccess", "PDKs");   // see note in Step 4

    [Theory]
    [InlineData("demo-pdk.json")]
    [InlineData("siepic-ebeam-pdk.json")]
    public void BundledPdk_HasSpecifiedProcessFingerprint(string file)
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, file));
        ProcessFingerprintFactory.From(draft).IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void DemoAndSiepic_ShareOneProcessGroup()
    {
        var loader = new PdkLoader();
        var entries = new[] { "demo-pdk.json", "siepic-ebeam-pdk.json" }
            .Select(f => loader.LoadFromFile(Path.Combine(PdkDir, f)))
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d)));

        ProcessCatalog.BuildGroups(entries).Count.ShouldBe(1);
    }
}
```

- [ ] **Step 4: Verify `PdkLoader.LoadFromFile` + `TestPaths.RepoRoot`**

Run: `codegraph_search PdkLoader` — confirm the public load method name; if it differs (e.g. `Load(string)`), use that. For the repo-root path helper, reuse the existing pattern from another test that reads `CAP-DataAccess/PDKs` (search: `grep -rn "CAP-DataAccess.*PDKs" UnitTests`); if no helper exists, resolve via `AppContext.BaseDirectory` walking up to the `.sln` (same approach as `UiScreenshotTests.ResolveOutputDirectory`). Replace `TestPaths.RepoRoot` accordingly — do NOT introduce a new helper if one exists.

- [ ] **Step 5: Run to verify pass** — `... BundledPdkProcess` → PASS (3 cases).

- [ ] **Step 6: Commit**

```bash
git add CAP-DataAccess/PDKs/demo-pdk.json CAP-DataAccess/PDKs/siepic-ebeam-pdk.json UnitTests/Components/Process/BundledPdkProcessTests.cs
git commit -m "(+) Single-process: declare process fingerprints on bundled PDKs (#570)"
```

---

## Phase 3 — Persistence & active-process state

### Task 6: `.lun` persistence — `ActiveProcessData` + save/load/migrate

**Files:**
- Modify: `CAP.Avalonia/ViewModels/MainViewModel.cs` (add `ActiveProcessData` DTO near `DesignFileData`, add `DesignFileData.ActiveProcess`)
- Create: `CAP.Avalonia/ViewModels/Panels/ActiveProcessResolver.cs` (pure mapping + migration helper, keeps `FileOperationsViewModel` small)
- Test: `UnitTests/ViewModels/Panels/ActiveProcessResolverTests.cs`

**Interfaces:**
- Produces: `class ActiveProcessData { string DisplayName; bool IsPlayground; string? CoreMaterial; double? CoreThicknessNm; string? Cladding; int DesignWavelengthNm; string? ProcessName; List<string> MemberPdkNames; }` (JSON-serialisable).
- Produces: `DesignFileData.ActiveProcess` (`ActiveProcessData?`).
- Produces: `static class ActiveProcessResolver`:
  - `ActiveProcessData? ToData(ActiveProcessSelection? sel)`
  - `ActiveProcessSelection? FromData(ActiveProcessData? data)`
  - `ActiveProcessSelection? Migrate(IEnumerable<string?> componentPdkSources, IReadOnlyList<ProcessGroup> catalog, out string? warning)` — single group → that process; multiple → Playground + warning; none → null.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

public class ActiveProcessResolverTests
{
    private static ProcessGroup Soi => new("SOI 220",
        new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo", "SiEPIC" });
    private static ProcessGroup Inp => new("InP",
        new ProcessFingerprint("InP", 300, "InP", 1550, "InP"), new[] { "HHI" });

    [Fact]
    public void RoundTrip_RealProcess_PreservesMembersAndName()
    {
        var sel = ActiveProcessSelection.ForGroup(Soi);
        var back = ActiveProcessResolver.FromData(ActiveProcessResolver.ToData(sel));
        back!.DisplayName.ShouldBe("SOI 220");
        back.MemberPdkNames.ShouldBe(new[] { "Demo", "SiEPIC" });
        back.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void RoundTrip_Playground_IsPreserved()
    {
        var back = ActiveProcessResolver.FromData(
            ActiveProcessResolver.ToData(ActiveProcessSelection.Playground()));
        back!.IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public void Migrate_AllComponentsOneGroup_AdoptsThatProcess()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "SiEPIC", null }, new[] { Soi, Inp }, out var warning);
        sel!.DisplayName.ShouldBe("SOI 220");
        warning.ShouldBeNull();
    }

    [Fact]
    public void Migrate_ComponentsSpanGroups_FallsBackToPlaygroundWithWarning()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "HHI" }, new[] { Soi, Inp }, out var warning);
        sel!.IsPlayground.ShouldBeTrue();
        warning.ShouldNotBeNull();
        warning!.ShouldContain("SOI 220");
        warning.ShouldContain("InP");
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `... ActiveProcessResolver` → FAIL.

- [ ] **Step 3: Add DTO + field in `MainViewModel.cs`** — after `DesignFileData.ChipHeightMicrometers` (around line 730) add:

```csharp
    /// <summary>
    /// The fabrication process this design is locked to (issue #570 — one process per chip).
    /// Null for legacy files saved before single-process support; migrated on load.
    /// </summary>
    public ActiveProcessData? ActiveProcess { get; set; }
```

and add a new top-level class next to `DesignFileData`:

```csharp
/// <summary>Serialisable form of the active process selection (issue #570).</summary>
public class ActiveProcessData
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPlayground { get; set; }
    public string? CoreMaterial { get; set; }
    public double? CoreThicknessNm { get; set; }
    public string? Cladding { get; set; }
    public int DesignWavelengthNm { get; set; } = 1550;
    public string? ProcessName { get; set; }
    public List<string> MemberPdkNames { get; set; } = new();
}
```

- [ ] **Step 4: Implement `ActiveProcessResolver`**

```csharp
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Process;

namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Maps the active-process selection to/from its persisted form and infers it for legacy
/// files that predate single-process support (issue #570).
/// </summary>
public static class ActiveProcessResolver
{
    /// <summary>Serialises a selection, or null when none is set.</summary>
    public static ActiveProcessData? ToData(ActiveProcessSelection? sel) => sel == null ? null : new ActiveProcessData
    {
        DisplayName = sel.DisplayName,
        IsPlayground = sel.IsPlayground,
        CoreMaterial = sel.Fingerprint?.CoreMaterial,
        CoreThicknessNm = sel.Fingerprint?.CoreThicknessNm,
        Cladding = sel.Fingerprint?.Cladding,
        DesignWavelengthNm = sel.Fingerprint?.DesignWavelengthNm ?? 1550,
        ProcessName = sel.Fingerprint?.ProcessName,
        MemberPdkNames = sel.MemberPdkNames.ToList(),
    };

    /// <summary>Deserialises a persisted selection, or null.</summary>
    public static ActiveProcessSelection? FromData(ActiveProcessData? data)
    {
        if (data == null) return null;
        if (data.IsPlayground) return ActiveProcessSelection.Playground();
        var fp = new ProcessFingerprint(data.CoreMaterial, data.CoreThicknessNm, data.Cladding,
            data.DesignWavelengthNm, data.ProcessName);
        return new ActiveProcessSelection(data.DisplayName, fp, data.MemberPdkNames, IsPlayground: false);
    }

    /// <summary>
    /// Infers the active process for a legacy design from the PDK sources of its placed
    /// components. One matching group → that process; several → Playground + a warning;
    /// none → null (empty / built-ins only).
    /// </summary>
    public static ActiveProcessSelection? Migrate(
        IEnumerable<string?> componentPdkSources,
        IReadOnlyList<ProcessGroup> catalog,
        out string? warning)
    {
        warning = null;
        var pdkNames = componentPdkSources
            .Where(s => !SingleProcessPolicy.IsBuiltIn(s))
            .Select(s => s!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pdkNames.Count == 0) return null;

        var matched = catalog
            .Where(g => g.MemberPdkNames.Any(m => pdkNames.Contains(m, System.StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 1)
            return ActiveProcessSelection.ForGroup(matched[0]);

        warning = "This design contains components from multiple processes " +
            $"({string.Join(", ", matched.Select(g => g.DisplayName))}). Opened in Playground — " +
            "not manufacturable. Remove conflicting components or start a new design.";
        return ActiveProcessSelection.Playground();
    }
}
```

- [ ] **Step 5: Run to verify pass** — `... ActiveProcessResolver` → PASS (4 tests). Then `dotnet build` to confirm the DTO change compiles.

- [ ] **Step 6: Commit**

```bash
git add CAP.Avalonia/ViewModels/MainViewModel.cs CAP.Avalonia/ViewModels/Panels/ActiveProcessResolver.cs UnitTests/ViewModels/Panels/ActiveProcessResolverTests.cs
git commit -m "(+) Single-process: persist + migrate active process in .lun (#570)"
```

---

### Task 7: Wire active process into `FileOperationsViewModel`

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Panels/FileOperationsViewModel.cs`
- Test: `UnitTests/ViewModels/Panels/FileOperationsActiveProcessTests.cs`

**Interfaces:**
- Consumes: `ActiveProcessResolver`, `ActiveProcessSelection`, `IReadOnlyList<ProcessGroup>`.
- Produces on `FileOperationsViewModel`:
  - `ActiveProcessSelection? ActiveProcess { get; private set; }`
  - `Func<IReadOnlyList<ProcessGroup>>? ProcessCatalogProvider { get; set; }` (wired by DI/MainViewModel to the live catalog)
  - `Action<string>? OnProcessMigrationWarning { get; set; }`
  - `void SetActiveProcess(ActiveProcessSelection? selection)`
  - Save writes `designData.ActiveProcess = ActiveProcessResolver.ToData(ActiveProcess);`
  - Load sets `ActiveProcess` from stored data, else `ActiveProcessResolver.Migrate(...)` using the placed components' `PdkSource`s + `ProcessCatalogProvider()`; a non-null warning is routed to `OnProcessMigrationWarning`.
  - `NewProject` no longer resets to null blindly — it leaves `ActiveProcess` for the New-Design dialog (Task 9) to set via `SetActiveProcess`.

- [ ] **Step 1: Write the failing test** (uses the existing `FileOperationsViewModel` test-construction pattern — check `UnitTests/ViewModels/Panels/` for an existing constructor helper and reuse it):

```csharp
// Verifies save→load round-trips the active process, and legacy load migrates.
// (Construct FileOperationsViewModel via the existing test helper in this folder;
//  if none exists, follow the arrangement used by other FileOperationsViewModel tests.)
[Fact]
public void SetActiveProcess_ThenSaveLoad_RestoresSelection() { /* arrange vm, SetActiveProcess(playground), save to temp .lun, new vm, load, assert ActiveProcess.IsPlayground */ }
```

Note: fill this in against the real constructor. If `FileOperationsViewModel` has no unit-test seam for save/load (it may require a canvas + services), prefer testing the pure `ActiveProcessResolver` (Task 6, already covered) and add a **thin** integration test only if a seam exists. Do not add production test-only methods.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement the wiring** in `FileOperationsViewModel.cs`:
  - Add the properties above.
  - In `SaveToFile`, next to `designData.ChipHeightMicrometers = …;` add `designData.ActiveProcess = ActiveProcessResolver.ToData(ActiveProcess);`.
  - In `LoadDesign`, after metadata restore, add:

```csharp
        var storedProcess = ActiveProcessResolver.FromData(designData.ActiveProcess);
        if (storedProcess != null)
        {
            ActiveProcess = storedProcess;
        }
        else
        {
            var catalog = ProcessCatalogProvider?.Invoke() ?? System.Array.Empty<ProcessGroup>();
            var pdkSources = designData.Components.Select(c => c.PdkSource)
                .Concat(designData.Groups?.SelectMany(g => g.ChildComponents.Select(ch => ch.PdkSource))
                        ?? System.Linq.Enumerable.Empty<string?>());
            ActiveProcess = ActiveProcessResolver.Migrate(pdkSources, catalog, out var warning);
            if (warning != null) OnProcessMigrationWarning?.Invoke(warning);
        }
```

  - Add `public void SetActiveProcess(ActiveProcessSelection? selection) { ActiveProcess = selection; HasUnsavedChanges = true; }`.
  - Remove the `ActivePdkName = null;` line from `NewProject` if present (there is none on `main`; #602's is not merged) — `NewProject` leaves `ActiveProcess` to the dialog.

- [ ] **Step 4: Run tests + `dotnet build`.**

- [ ] **Step 5: Commit**

```bash
git add CAP.Avalonia/ViewModels/Panels/FileOperationsViewModel.cs UnitTests/ViewModels/Panels/FileOperationsActiveProcessTests.cs
git commit -m "(+) Single-process: FileOperations tracks/persists/migrates active process (#570)"
```

---

## Phase 4 — UI: selection, indicator, library filter

### Task 8: New-Design process-selection dialog

**Files:**
- Create: `CAP.Avalonia/ViewModels/Process/ProcessSelectionViewModel.cs`
- Create: `CAP.Avalonia/Views/ProcessSelectionDialog.axaml` (+ `.axaml.cs`)
- Test: `UnitTests/ViewModels/Process/ProcessSelectionViewModelTests.cs`

**Interfaces:**
- Consumes: `IReadOnlyList<ProcessGroup>`, `ActiveProcessSelection`.
- Produces: `ProcessSelectionViewModel(IReadOnlyList<ProcessGroup> groups)` with:
  - `ObservableCollection<ProcessChoiceItem> Choices` (one per group + a trailing Playground item)
  - `ProcessChoiceItem? SelectedChoice`
  - `ActiveProcessSelection? Result` (null until confirmed)
  - `IRelayCommand ConfirmCommand` (sets `Result` from `SelectedChoice`), `bool CanConfirm`
  - `record ProcessChoiceItem(string Title, string Subtitle, ActiveProcessSelection Selection, bool IsPlayground)`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using CAP.Avalonia.ViewModels.Process;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Process;

public class ProcessSelectionViewModelTests
{
    private static ProcessGroup Soi => new("SOI 220",
        new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo", "SiEPIC" });

    [Fact]
    public void Choices_ListGroupsPlusPlayground()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.Choices.Count.ShouldBe(2);
        vm.Choices.Last().IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public void Confirm_WithGroup_SetsRealProcessResult()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.SelectedChoice = vm.Choices.First();
        vm.ConfirmCommand.Execute(null);
        vm.Result!.DisplayName.ShouldBe("SOI 220");
        vm.Result.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void Confirm_WithoutSelection_DoesNothing()
    {
        var vm = new ProcessSelectionViewModel(new[] { Soi });
        vm.CanConfirm.ShouldBeFalse();
        vm.ConfirmCommand.Execute(null);
        vm.Result.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement `ProcessSelectionViewModel`**

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CAP_Core.Components.Process;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Process;

/// <summary>One selectable option in the New-Design process dialog (issue #570).</summary>
public sealed record ProcessChoiceItem(string Title, string Subtitle, ActiveProcessSelection Selection, bool IsPlayground);

/// <summary>
/// Lets the user consciously pick the fabrication process for a new design, or Playground
/// (mix anything, not manufacturable). Produces an <see cref="ActiveProcessSelection"/> (#570).
/// </summary>
public partial class ProcessSelectionViewModel : ObservableObject
{
    /// <summary>Available processes (derived groups) plus a trailing Playground option.</summary>
    public ObservableCollection<ProcessChoiceItem> Choices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private ProcessChoiceItem? _selectedChoice;

    /// <summary>The confirmed selection, or null until the user confirms.</summary>
    public ActiveProcessSelection? Result { get; private set; }

    /// <summary>True when a choice is selected.</summary>
    public bool CanConfirm => SelectedChoice != null;

    /// <summary>Builds the choice list from the derived process groups.</summary>
    public ProcessSelectionViewModel(IReadOnlyList<ProcessGroup> groups)
    {
        foreach (var g in groups)
            Choices.Add(new ProcessChoiceItem(
                g.DisplayName,
                $"{g.MemberPdkNames.Count} PDK(s): {string.Join(", ", g.MemberPdkNames)}",
                ActiveProcessSelection.ForGroup(g), IsPlayground: false));

        Choices.Add(new ProcessChoiceItem(
            "Playground", "Mix any components — not manufacturable",
            ActiveProcessSelection.Playground(), IsPlayground: true));
    }

    /// <summary>Confirms the current selection into <see cref="Result"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Result = SelectedChoice?.Selection;
}
```

- [ ] **Step 4: Implement the dialog view** — `CAP.Avalonia/Views/ProcessSelectionDialog.axaml`: a `Window` (`x:DataType="vmp:ProcessSelectionViewModel"`, `WindowStartupLocation=CenterOwner`) with a heading "Choose fabrication process", a `ListBox` bound to `Choices`/`SelectedChoice` (item template: `Title` bold + `Subtitle` gray; Playground item styled with a ⚠), and a "Start design" `Button` bound to `ConfirmCommand` that closes the window on confirm. Code-behind closes the dialog when `Confirm` runs (subscribe to `ConfirmCommand` or handle the button click after execute). Follow the existing dialog pattern in `CAP.Avalonia/Views/ComponentSettingsDialog.axaml(.cs)` for show/close mechanics and `x:CompileBindings`.

- [ ] **Step 5: Run tests + build.**

- [ ] **Step 6: Commit**

```bash
git add CAP.Avalonia/ViewModels/Process/ProcessSelectionViewModel.cs CAP.Avalonia/Views/ProcessSelectionDialog.axaml CAP.Avalonia/Views/ProcessSelectionDialog.axaml.cs UnitTests/ViewModels/Process/ProcessSelectionViewModelTests.cs
git commit -m "(+) Single-process: New-Design process-selection dialog (#570)"
```

---

### Task 9: New-Design flow + active-process indicator (MainViewModel)

**Files:**
- Modify: `CAP.Avalonia/ViewModels/MainViewModel.cs` (catalog provider, New-Design hook, indicator property, migration-warning routing)
- Modify: `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs` (expose loaded PDK fingerprints for the catalog)
- Modify: `CAP.Avalonia/Views/MainWindow.axaml` (indicator chip)
- Test: `UnitTests/ViewModels/MainViewModelProcessTests.cs`

**Interfaces:**
- Consumes: `ProcessCatalog.BuildGroups`, `ProcessFingerprintFactory.From`, `ProcessSelectionViewModel`, `FileOperationsViewModel.{ActiveProcess, SetActiveProcess, ProcessCatalogProvider, OnProcessMigrationWarning}`.
- Produces on `LeftPanelViewModel`: `IReadOnlyList<PdkProcessEntry> GetLoadedPdkProcessEntries()` (built from each loaded `PdkDraft` via `ProcessFingerprintFactory.From`). Requires keeping the loaded `PdkDraft`s (or their `(Name, PdkDraft)`) — `LoadBundledPdks` already loads them; store the drafts in a private list during load.
- Produces on `MainViewModel`: `string ActiveProcessLabel` + `bool IsPlayground` (bindable, updated from `FileOperations.ActiveProcess`); `ProcessCatalogProvider` wired to `() => ProcessCatalog.BuildGroups(LeftPanel.GetLoadedPdkProcessEntries())`; New-Design shows `ProcessSelectionDialog` and calls `FileOperations.SetActiveProcess(result)`.

- [ ] **Step 1: Write the failing test** — the pure, testable slice is the catalog wiring + indicator label:

```csharp
// Given a MainViewModel with loaded bundled PDKs, ProcessCatalogProvider returns >=1 group,
// and after SetActiveProcess(playground) the ActiveProcessLabel reads "Playground" and IsPlayground is true.
// Construct MainViewModel via the existing test helper (UnitTests/Helpers/MainViewModelTestHelper.cs).
```

Use `MainViewModelTestHelper.CreateMainViewModel()` (already used by `UiScreenshotTests`). Assert `vm.ProcessCatalogProvider` (or the equivalent wired provider) yields groups and the indicator label updates. Keep assertions to observable VM state.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement**
  - `LeftPanelViewModel`: during `LoadBundledPdks`, keep each loaded `PdkDraft` in a `private readonly List<PdkDraft> _loadedPdkDrafts`. Add:

```csharp
    /// <summary>Process fingerprints of all loaded PDKs, for single-process grouping (#570).</summary>
    public IReadOnlyList<PdkProcessEntry> GetLoadedPdkProcessEntries() =>
        _loadedPdkDrafts.Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d))).ToList();
```

  - `MainViewModel`: after existing wiring, add:

```csharp
    FileOperations.ProcessCatalogProvider = () =>
        ProcessCatalog.BuildGroups(LeftPanel.GetLoadedPdkProcessEntries());
    FileOperations.OnProcessMigrationWarning = msg => UpdateStatus(msg);   // reuse existing status/log sink
    FileOperations.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(FileOperations.ActiveProcess)) RefreshProcessIndicator();
    };
```

  add bindable indicator members:

```csharp
    [ObservableProperty] private string _activeProcessLabel = "No process selected";
    [ObservableProperty] private bool _isPlayground;

    private void RefreshProcessIndicator()
    {
        var p = FileOperations.ActiveProcess;
        IsPlayground = p?.IsPlayground == true;
        ActiveProcessLabel = p == null ? "No process selected"
            : p.IsPlayground ? "Playground — not manufacturable"
            : $"Process: {p.DisplayName}";
    }
```

  (Make `ActiveProcess` raise `PropertyChanged` — either make `FileOperationsViewModel` set it via an `[ObservableProperty]` backing field, or call `OnPropertyChanged(nameof(ActiveProcess))` in `SetActiveProcess`/load. Prefer `[ObservableProperty] private ActiveProcessSelection? _activeProcess;` with a `private set` exposed through the generated property — keep the public setter internal to the VM.)
  - New-Design hook: where `NewProjectCommand`/`FileOperations` triggers a new design, show `ProcessSelectionDialog` (built from `ProcessCatalogProvider()`), await the `Result`, and call `FileOperations.SetActiveProcess(result)`. If the user cancels, abort New Design. Follow the existing async dialog-show pattern in `MainViewModel`/`MainWindow.axaml.cs`.

  - `MainWindow.axaml`: add a persistent indicator chip in the top toolbar bound to `ActiveProcessLabel`, with a warning brush when `IsPlayground` (use a `IValueConverter` or a `DataTrigger`/`Classes.playground` style already-present pattern). Tooltip binds to the member-PDK list.

- [ ] **Step 4: Run tests + build + launch smoke** — build the app, open it, `File → New`, confirm the process dialog appears and the indicator updates. (Use the run skill / `Start-Process` on the built exe.)

- [ ] **Step 5: Commit**

```bash
git add CAP.Avalonia/ViewModels/MainViewModel.cs CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs CAP.Avalonia/Views/MainWindow.axaml UnitTests/ViewModels/MainViewModelProcessTests.cs
git commit -m "(+) Single-process: New-Design picker + active-process indicator (#570)"
```

---

### Task 10: Library filter follows the active process

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs` (+ `PdkManagerViewModel` if a "lock toggles" flag is needed)
- Test: `UnitTests/ViewModels/Panels/LibraryProcessFilterTests.cs`

**Interfaces:**
- Consumes: `ActiveProcessSelection`.
- Produces on `LeftPanelViewModel`: `void ApplyActiveProcess(ActiveProcessSelection? active)` — real process → enable exactly its `MemberPdkNames` (+ built-in), and set `PdkManager.ManualTogglesEnabled = false`; Playground/null → `ManualTogglesEnabled = true`, keep current manual enables. Wired from `MainViewModel` on `ActiveProcess` change.
- Produces on `PdkManagerViewModel`: `bool ManualTogglesEnabled` (`[ObservableProperty]`, default true) to hide/disable per-PDK checkboxes in the PDK-manager UI; `void SetEnabledPdks(IEnumerable<string> names)` to drive the enabled set.

- [ ] **Step 1: Write the failing test**

```csharp
// With two loaded PDKs "Demo" and "HHI", ApplyActiveProcess(SOI group with members {"Demo"})
// → FilteredTemplates contains only Demo (+ built-in) components and PdkManager.ManualTogglesEnabled == false.
// ApplyActiveProcess(Playground) → ManualTogglesEnabled == true.
// Construct LeftPanelViewModel via its existing test arrangement.
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement**
  - `PdkManagerViewModel`: add `[ObservableProperty] private bool _manualTogglesEnabled = true;` and `public void SetEnabledPdks(IEnumerable<string> names) { /* set each LoadedPdks[i].IsEnabled = names.Contains(name); */ OnFilterChanged?.Invoke(); }`.
  - `LeftPanelViewModel.ApplyActiveProcess`:

```csharp
    /// <summary>Drives the library filter to the active process's PDKs (issue #570).</summary>
    public void ApplyActiveProcess(ActiveProcessSelection? active)
    {
        if (active is { IsPlayground: false })
        {
            PdkManager.SetEnabledPdks(active.MemberPdkNames);
            PdkManager.ManualTogglesEnabled = false;
        }
        else
        {
            PdkManager.ManualTogglesEnabled = true;   // Playground / none: user controls the toggles
        }
        FilterComponents();
    }
```

  - `MainViewModel`: in `RefreshProcessIndicator` (Task 9) also call `LeftPanel.ApplyActiveProcess(FileOperations.ActiveProcess)`.
  - PDK-manager AXAML: bind the per-PDK toggle `IsEnabled`/`IsVisible` to `ManualTogglesEnabled`.

- [ ] **Step 4: Run tests + build.**

- [ ] **Step 5: Commit**

```bash
git add CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs CAP.Avalonia/ViewModels/Library/PdkManagerViewModel.cs CAP.Avalonia/Views/**/Pdk*.axaml UnitTests/ViewModels/Panels/LibraryProcessFilterTests.cs
git commit -m "(+) Single-process: library filter follows the active process (#570)"
```

---

## Phase 5 — Enforcement wiring

### Task 11: Enforce at placement and paste

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Panels/CanvasInteractionViewModel.cs`
- Modify: `CAP.Avalonia/ViewModels/MainViewModel.cs` (wire `GetActiveProcess`)
- Test: `UnitTests/ViewModels/Panels/CanvasInteractionProcessEnforcementTests.cs`

**Interfaces:**
- Consumes: `SingleProcessPolicy.CheckPlacement`, `ActiveProcessSelection`, `ComponentTemplate.PdkSource`.
- Produces on `CanvasInteractionViewModel`: `Func<ActiveProcessSelection?>? GetActiveProcess { get; set; }`; `PlaceComponentAt` blocks a foreign-process template before executing the command (status message, no undo entry); `OnComponentsPasted` (or the paste handler) filters foreign-process components and reports how many were skipped.

- [ ] **Step 1: Write the failing test**

```csharp
// Arrange a CanvasInteractionViewModel with GetActiveProcess returning a SOI process whose
// members = {"Demo"}. SelectedTemplate has PdkSource "HHI-InP". PlaceComponentAt(x,y) must NOT
// add a component and must set a status message mentioning the block. With PdkSource "Demo"
// (a member) or "Built-in", the component IS placed.
// Construct via the existing CanvasInteractionViewModel test arrangement (see sibling tests).
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** in `CanvasInteractionViewModel.PlaceComponentAt` (replace #602-style check with the process policy):

```csharp
        var (isAllowed, blockReason) = SingleProcessPolicy.CheckPlacement(
            GetActiveProcess?.Invoke(), SelectedTemplate.PdkSource);
        if (!isAllowed)
        {
            UpdateStatus?.Invoke(blockReason ?? "PDK/process mismatch — cannot place component.");
            return;
        }
```

  and in the paste path (`OnComponentsPasted` consumer / wherever pasted components are materialised), filter each pasted component through `SingleProcessPolicy.CheckPlacement(GetActiveProcess?.Invoke(), pastedPdkSource)`; skip disallowed ones and `UpdateStatus` with a "skipped N components from another process" summary. (If the paste materialisation lives outside this VM, add the guard where pasted `ComponentViewModel`s are created — search: `grep -rn "OnComponentsPasted" CAP.Avalonia`.)
  Add `public Func<ActiveProcessSelection?>? GetActiveProcess { get; set; }`. In `MainViewModel`, wire `CanvasInteraction.GetActiveProcess = () => FileOperations.ActiveProcess;`.

- [ ] **Step 4: Run tests + build + launch smoke** — place a foreign-process component → blocked with a message; a member/built-in → placed.

- [ ] **Step 5: Commit**

```bash
git add CAP.Avalonia/ViewModels/Panels/CanvasInteractionViewModel.cs CAP.Avalonia/ViewModels/MainViewModel.cs UnitTests/ViewModels/Panels/CanvasInteractionProcessEnforcementTests.cs
git commit -m "(+) Single-process: enforce active process on placement + paste (#570)"
```

---

## Phase 6 — Finish

### Task 12: Full suite, PR, close #602

- [ ] **Step 1:** `dotnet build ConnectAPICPro.sln` — zero errors.
- [ ] **Step 2:** `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py"` — full suite green (the two known Windows-local failures — `AppDataFoldersTests.ResolveLinux_...`, `PhotonTorch…MemoryError` — are pre-existing and CI-irrelevant; everything else passes).
- [ ] **Step 3:** Regenerate the UI screenshot (`dotnet test UnitTests --filter Category=UiScreenshots`) and eyeball `PythonEnvironmentManagerPanel.png`… and add a `MainWindow.png` glance for the new indicator if the harness covers it.
- [ ] **Step 4:** Open the PR (base `main`), body summarising the single-process model + linking issue #570 and the spec. Ensure CI (`🔍 xUnit Tests`) is green before merge.
- [ ] **Step 5:** After merge, close PR #602 as superseded (its PDK-name lock is replaced by the process-keyed model), deleting its branch.

---

## Self-Review notes (author)

- **Spec coverage:** (A) Tasks 4–5; (B) Tasks 2, 5; (C) Tasks 8–10; (D) Tasks 11, 6 (migration). Indicator = Task 9. Non-goal (E/FDTD) excluded.
- **Type consistency:** `ProcessFingerprint`, `ProcessGroup`, `PdkProcessEntry`, `ActiveProcessSelection`, `ProcessCatalog.BuildGroups`, `SingleProcessPolicy.CheckPlacement`, `ProcessFingerprintFactory.From`, `ActiveProcessResolver.{ToData,FromData,Migrate}`, `ActiveProcessData`, `FileOperationsViewModel.{ActiveProcess,SetActiveProcess,ProcessCatalogProvider,OnProcessMigrationWarning}`, `LeftPanelViewModel.{GetLoadedPdkProcessEntries,ApplyActiveProcess}`, `PdkManagerViewModel.{ManualTogglesEnabled,SetEnabledPdks}`, `CanvasInteractionViewModel.GetActiveProcess` — names used consistently across tasks.
- **Known unknowns to resolve during execution (flagged inline, not placeholders):** exact `PdkLoader` load-method name (Task 5), the repo-root test-path helper (Task 5), `FileOperationsViewModel`/`CanvasInteractionViewModel`/`LeftPanelViewModel` unit-test construction seams (Tasks 7, 10, 11), and where pasted components are materialised (Task 11). Each step says how to find the real symbol before writing code.

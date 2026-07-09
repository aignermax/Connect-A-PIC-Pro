# Bring-your-own-Component → eigenes PDK (v1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein „+ Neue Komponente"-Fenster in der PDK-Library, mit dem Nutzer eine eigene Komponente über eine gdsfactory-/nazca-Funktionsreferenz anlegen, ihre S-Matrix mit dem vorhandenen FDTD/Meep-Solver berechnen (oder als Blackbox/2-Port-Ideal speichern) und in ein automatisch angelegtes User-PDK pro Prozess ablegen.

**Architecture:** Neuer Vertical Slice `AddCustomComponent`. Reine Persistenz (`UserPdkStore`) liegt in CAP-DataAccess; UI-koordinierende Services (`ComponentGeometryExtractor`, `FdtdSMatrixToDraftConverter`) und das ViewModel/Fenster in CAP.Avalonia. Wiederverwendet die bestehende FDTD-Pipeline (`IFdtdSMatrixService`, `FdtdSMatrixConverter`), die Preview-Services (`NazcaComponentPreviewService`, `GdsFactoryComponentPreviewService`), `OverridePinMapper`, `PdkJsonSaver`/`PdkLoader`, `PdkTemplateConverter` und die `LeftPanelViewModel`-Library.

**Tech Stack:** C# / .NET 10 / Avalonia 11 / CommunityToolkit.Mvvm; xUnit + Shouldly + Moq; Python-Subprozess-Preview + Docker-Meep-FDTD (beide bereits vorhanden).

## Global Constraints

- Keine erfundene Physik: S-Matrix nur aus FDTD-Meep (real), Blackbox (leer), oder verlustfreiem 2-Port-Ideal. Nie erfundene Werte; FDTD-Fehler → nichts speichern.
- Cross-Platform-Parität: Pfade via `Path.Combine` + `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)`; keine Drive-Letter/`\`-Literale. Kein direkter `Process.Start` (nur die vorhandenen Abstraktionen). Maschinennahe Strings (Slug, JSON) mit `CultureInfo.InvariantCulture`.
- Gebündelte Foundry-JSONs (`CAP-DataAccess/PDKs/*.json`) werden NIE geschrieben. User-PDKs liegen ausschließlich unter `%LOCALAPPDATA%/Lunima/user-pdks/`.
- Max. 250 Zeilen pro NEUER Datei. XML-Doku auf public members. `[ObservableProperty]`/`[RelayCommand]` im ViewModel. Neuer Service in DI via Feature-Extension in `CAP.Avalonia/DI/`, aufgerufen aus `App.axaml.cs`.
- Tests deterministisch; plattform-spezifische Pfad-Assertions mit `OperatingSystem.IsX()` guarden (Linux-CI grün). Preview-/FDTD-Subprozesse in Unit-Tests immer mocken.
- Vertical-Slice-Import-Regeln (CLAUDE.md): der Slice importiert nur eigene Namespaces, den Shared-Kernel und Framework-Namespaces.

## File Structure

- `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs` — Auflösung des User-PDK-Pfads pro Prozess; Laden-oder-Anlegen, Komponente hinzufügen/ersetzen, Speichern.
- `CAP.Avalonia/Services/AddCustomComponent/GeometryReference.cs` — Wertobjekt (Backend, Modul, Funktion, Parameter) + Erzeugung des gdsfactory-Rawcode-Wrappers.
- `CAP.Avalonia/Services/AddCustomComponent/ComponentGeometryExtractor.cs` — rendert eine Referenz über den passenden Preview-Service → bbox + Pins + rohes `NazcaPreviewResult`.
- `CAP.Avalonia/Services/AddCustomComponent/FdtdSMatrixToDraftConverter.cs` — `ComponentSMatrixData` → `PdkSMatrixDraft`; Blackbox → null; 2-Port-Ideal → verlustfreies Pass-through.
- `CAP.Avalonia/Services/Solvers/ComponentFdtdRequestFactory.cs` (MODIFY) — die private Polygon-/Port-Baulogik in eine statische `BuildFromPreview(...)` herauslösen, damit der neue Flow denselben Code nutzt.
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` — Orchestrierung (Name/Backend/Referenz/Prozess/Preview/FDTD/Speichern).
- `CAP.Avalonia/Views/NewComponentWindow.axaml` (+ `.axaml.cs`) — das Fenster.
- `CAP.Avalonia/DI/AddCustomComponentFeature.cs` — DI-Extension.
- `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs` (MODIFY) — `[RelayCommand] OpenNewComponent` + Registrierung des gespeicherten Drafts in `AllTemplates`.
- `CAP.Avalonia/Views/MainWindow.axaml` (MODIFY) — „+"-Button an der Komponenten-Library.
- `CAP.Avalonia/App.axaml.cs` (MODIFY) — `services.AddAddCustomComponentFeature();`
- Tests unter `UnitTests/Components/AddCustomComponent/` und `UnitTests/Architecture/`.

---

### Task 1: `UserPdkStore` (Persistenz pro Prozess)

**Files:**
- Create: `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs`
- Test: `UnitTests/Components/AddCustomComponent/UserPdkStoreTests.cs`

**Interfaces:**
- Consumes: `PdkJsonSaver.SaveToFile(PdkDraft pdk, string filePath) : void`; `PdkLoader.LoadFromFileForEditing(string) : PdkDraft`; DTOs `PdkDraft` (Props: `Name`, `Foundry`, `Backend`, `Process`, `GdsFactoryRoutingCrossSection`, `Components`), `PdkComponentDraft` (Prop `Name`), `ProcessDefinition` (Prop `Name`).
- Produces:
  - `UserPdkStore(string userPdkRootDirectory, PdkJsonSaver saver, PdkLoader loader)`
  - `static UserPdkStore CreateDefault()` — root = `%LOCALAPPDATA%/Lunima/user-pdks`
  - `string ResolvePath(ProcessDefinition process) : string`
  - `bool ComponentExists(ProcessDefinition process, string componentName) : bool`
  - `string Save(ProcessDefinition process, PdkComponentDraft component, string backend, string? routingCrossSection) : string` — legt an/lädt, ersetzt-oder-fügt-hinzu (Name-Match, Ordinal-IgnoreCase), speichert, gibt den Pfad zurück.

- [ ] **Step 1: Read the real signatures**

Lies vor dem Schreiben:
- `CAP-DataAccess/Components/ComponentDraftMapper/PdkJsonSaver.cs` (Methode `SaveToFile`).
- `CAP-DataAccess/Components/ComponentDraftMapper/PdkLoader.cs` (`LoadFromFileForEditing`, ctor — ist er parameterlos?).
- `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/PdkDraft.cs` (exakte Property-Namen von `PdkDraft`, `PdkComponentDraft`, `ProcessDefinition`).
Verwende die tatsächlichen Namen; unten stehende sind aus der Inventur, aber verifiziere sie.

- [ ] **Step 2: Write the failing tests**

```csharp
using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-userpdk-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore CreateStore() => new(_root, new PdkJsonSaver(), new PdkLoader());

    private static ProcessDefinition Process(string name) => new() { Name = name };
    private static PdkComponentDraft Comp(string name) => new() { Name = name, GdsFactoryFunction = "cspdk.sin300.coupler" };

    [Fact]
    public void ResolvePath_is_under_user_root_and_slugified()
    {
        var store = CreateStore();
        var path = store.ResolvePath(Process("CornerStone SiN 300"));
        path.ShouldStartWith(_root);
        Path.GetFileName(path).ShouldBe("cornerstone-sin-300.json");
    }

    [Fact]
    public void Save_creates_file_and_roundtrips_the_component()
    {
        var store = CreateStore();
        var path = store.Save(Process("CornerStone SiN 300"), Comp("My Coupler"), backend: "gdsfactory", routingCrossSection: "xs_nc");

        File.Exists(path).ShouldBeTrue();
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.ShouldContain(c => c.Name == "My Coupler");
        reloaded.Process!.Name.ShouldBe("CornerStone SiN 300");
        reloaded.Backend.ShouldBe("gdsfactory");
    }

    [Fact]
    public void Save_twice_same_name_replaces_not_duplicates()
    {
        var store = CreateStore();
        store.Save(Process("P"), Comp("X"), "gdsfactory", null);
        var path = store.Save(Process("P"), Comp("X"), "gdsfactory", null);

        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        reloaded.Components.FindAll(c => c.Name == "X").Count.ShouldBe(1);
    }

    [Fact]
    public void ComponentExists_reflects_saved_state()
    {
        var store = CreateStore();
        store.ComponentExists(Process("P"), "X").ShouldBeFalse();
        store.Save(Process("P"), Comp("X"), "gdsfactory", null);
        store.ComponentExists(Process("P"), "X").ShouldBeTrue();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" UserPdkStore`
Expected: FAIL (Typ `UserPdkStore` existiert nicht).

- [ ] **Step 4: Implement `UserPdkStore`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.AddCustomComponent;

/// <summary>
/// Persists user-authored components into a writable, per-process user PDK file under
/// the user's local app-data directory. Never touches the bundled foundry PDK JSONs.
/// One PDK file per fabrication process (the S-matrix is process-specific, #570).
/// </summary>
public sealed class UserPdkStore
{
    private readonly string _root;
    private readonly PdkJsonSaver _saver;
    private readonly PdkLoader _loader;

    /// <summary>Creates a store rooted at an explicit directory (used by tests).</summary>
    public UserPdkStore(string userPdkRootDirectory, PdkJsonSaver saver, PdkLoader loader)
    {
        _root = userPdkRootDirectory;
        _saver = saver;
        _loader = loader;
    }

    /// <summary>Creates a store rooted at %LOCALAPPDATA%/Lunima/user-pdks.</summary>
    public static UserPdkStore CreateDefault() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "user-pdks"),
        new PdkJsonSaver(), new PdkLoader());

    /// <summary>The user-PDK file path for a process (creates no file).</summary>
    public string ResolvePath(ProcessDefinition process) =>
        Path.Combine(_root, Slug(process.Name) + ".json");

    /// <summary>True when a component of that name is already stored for the process.</summary>
    public bool ComponentExists(ProcessDefinition process, string componentName)
    {
        var path = ResolvePath(process);
        if (!File.Exists(path)) return false;
        var pdk = _loader.LoadFromFileForEditing(path);
        return pdk.Components.Exists(c => string.Equals(c.Name, componentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds or replaces (by name, case-insensitive) the component in the process's user PDK,
    /// creating the PDK file on first use. Returns the file path written.
    /// </summary>
    public string Save(ProcessDefinition process, PdkComponentDraft component, string backend, string? routingCrossSection)
    {
        var path = ResolvePath(process);
        Directory.CreateDirectory(_root);

        var pdk = File.Exists(path) ? _loader.LoadFromFileForEditing(path) : NewPdk(process, backend, routingCrossSection);
        pdk.Components.RemoveAll(c => string.Equals(c.Name, component.Name, StringComparison.OrdinalIgnoreCase));
        pdk.Components.Add(component);

        _saver.SaveToFile(pdk, path);
        return path;
    }

    private static PdkDraft NewPdk(ProcessDefinition process, string backend, string? routingCrossSection) => new()
    {
        Name = $"My {process.Name} Components",
        Foundry = process.Foundry,
        Backend = backend,
        Process = process,
        GdsFactoryRoutingCrossSection = routingCrossSection,
        Components = new()
    };

    private static string Slug(string name)
    {
        var lower = (name ?? string.Empty).ToLower(CultureInfo.InvariantCulture);
        var slug = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "custom" : slug;
    }
}
```

Falls `PdkLoader` keinen parameterlosen ctor hat, passe die Tests + `CreateDefault` an den echten ctor an (Step 1 hat ihn ermittelt). Falls `ProcessDefinition.Foundry` nicht existiert, lasse `Foundry` weg.

- [ ] **Step 5: Run tests to verify they pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" UserPdkStore`
Expected: PASS (4/4).

- [ ] **Step 6: Commit**

```bash
git add CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs UnitTests/Components/AddCustomComponent/UserPdkStoreTests.cs
git commit -m "(+) UserPdkStore: per-process, writable user PDK persistence (never touches foundry JSONs)"
```

---

### Task 2: `FdtdSMatrixToDraftConverter` (ehrliche S-Matrix → Draft)

**Files:**
- Create: `CAP.Avalonia/Services/AddCustomComponent/FdtdSMatrixToDraftConverter.cs`
- Test: `UnitTests/Components/AddCustomComponent/FdtdSMatrixToDraftConverterTests.cs`

**Interfaces:**
- Consumes: `ComponentSMatrixData` (`Dictionary<string,SMatrixWavelengthEntry> Wavelengths`, `string? SourceNote`), `SMatrixWavelengthEntry` (`int Rows`, `int Cols`, `List<double> Real`, `List<double> Imag`, `List<string>? PortNames`). Ziel-DTO `PdkSMatrixDraft` (`int WavelengthNm`, `List<SMatrixConnection> Connections`, `List<WavelengthSMatrixEntry>? WavelengthData`) und `WavelengthSMatrixEntry`.
- Produces:
  - `static PdkSMatrixDraft? FromFdtd(ComponentSMatrixData data)` — null bei leeren Wavelengths.
  - `static PdkSMatrixDraft? BlackBox()` — gibt `null` zurück (kein Modell → Draft-Feld `SMatrix` bleibt null).
  - `static PdkSMatrixDraft LosslessTwoPort(string inPin, string outPin, int wavelengthNm)` — Betrag 1, Phase 0 in beide Richtungen.

- [ ] **Step 1: Read the real signatures**

Lies `CAP-DataAccess/Persistence/PIR/ComponentSMatrixData.cs`, `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/PdkDraft.cs` (Typen `PdkSMatrixDraft`, `SMatrixConnection`, `WavelengthSMatrixEntry`). Bestätige die exakte Form von `WavelengthSMatrixEntry` (Feldnamen für Wellenlänge/Real/Imag/Ports) und `SMatrixConnection` (`FromPin`, `ToPin`, `Magnitude`, `PhaseDegrees`).

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class FdtdSMatrixToDraftConverterTests
{
    [Fact]
    public void FromFdtd_maps_each_wavelength_entry()
    {
        var data = new ComponentSMatrixData
        {
            SourceNote = "FDTD Meep 2D",
            Wavelengths = new()
            {
                ["1550"] = new SMatrixWavelengthEntry
                {
                    Rows = 2, Cols = 2,
                    Real = new() { 0, 1, 1, 0 },
                    Imag = new() { 0, 0, 0, 0 },
                    PortNames = new() { "o1", "o2" }
                }
            }
        };

        var draft = FdtdSMatrixToDraftConverter.FromFdtd(data);

        draft.ShouldNotBeNull();
        draft!.WavelengthData!.Count.ShouldBe(1);
    }

    [Fact]
    public void FromFdtd_returns_null_when_no_wavelengths()
    {
        var data = new ComponentSMatrixData { Wavelengths = new() };
        FdtdSMatrixToDraftConverter.FromFdtd(data).ShouldBeNull();
    }

    [Fact]
    public void BlackBox_is_null_so_the_component_has_no_model()
    {
        FdtdSMatrixToDraftConverter.BlackBox().ShouldBeNull();
    }

    [Fact]
    public void LosslessTwoPort_is_unit_magnitude_both_directions()
    {
        var draft = FdtdSMatrixToDraftConverter.LosslessTwoPort("o1", "o2", 1550);
        draft.Connections.Count.ShouldBe(2);
        draft.Connections.ShouldContain(c => c.FromPin == "o1" && c.ToPin == "o2" && c.Magnitude == 1.0);
        draft.Connections.ShouldContain(c => c.FromPin == "o2" && c.ToPin == "o1" && c.Magnitude == 1.0);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" FdtdSMatrixToDraftConverter`
Expected: FAIL (Typ existiert nicht).

- [ ] **Step 4: Implement the converter**

Mappe jeden `ComponentSMatrixData.Wavelengths`-Eintrag (Key = nm-String) auf einen `WavelengthSMatrixEntry` des Drafts. Übertrage `Rows/Cols/Real/Imag/PortNames` 1:1 in die Feldnamen, die Step 1 ermittelt hat. Beispiel-Skelett (Feldnamen ggf. anpassen):

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Converts an FDTD-computed <see cref="ComponentSMatrixData"/> into the PDK draft's
/// <see cref="PdkSMatrixDraft"/>, and provides the two honest no-FDTD fallbacks:
/// a black-box (no model) and a lossless two-port pass-through. Never fabricates values.
/// </summary>
public static class FdtdSMatrixToDraftConverter
{
    /// <summary>Multi-wavelength draft from FDTD output; null if there is no data.</summary>
    public static PdkSMatrixDraft? FromFdtd(ComponentSMatrixData data)
    {
        if (data.Wavelengths == null || data.Wavelengths.Count == 0)
            return null;

        var entries = new List<WavelengthSMatrixEntry>();
        foreach (var kv in data.Wavelengths)
        {
            int nm = int.Parse(kv.Key, CultureInfo.InvariantCulture);
            entries.Add(new WavelengthSMatrixEntry
            {
                // Map to the real field names verified in Step 1:
                WavelengthNm = nm,
                Rows = kv.Value.Rows,
                Cols = kv.Value.Cols,
                Real = kv.Value.Real,
                Imag = kv.Value.Imag,
                PortNames = kv.Value.PortNames
            });
        }

        int firstNm = entries[0].WavelengthNm;
        return new PdkSMatrixDraft { WavelengthNm = firstNm, WavelengthData = entries };
    }

    /// <summary>No simulation model — the draft's SMatrix stays null (black box).</summary>
    public static PdkSMatrixDraft? BlackBox() => null;

    /// <summary>The honest lossless two-port pass-through (routing components).</summary>
    public static PdkSMatrixDraft LosslessTwoPort(string inPin, string outPin, int wavelengthNm) => new()
    {
        WavelengthNm = wavelengthNm,
        Connections = new()
        {
            new SMatrixConnection { FromPin = inPin, ToPin = outPin, Magnitude = 1.0, PhaseDegrees = 0.0 },
            new SMatrixConnection { FromPin = outPin, ToPin = inPin, Magnitude = 1.0, PhaseDegrees = 0.0 },
        }
    };
}
```

Falls `WavelengthSMatrixEntry` andere Feldnamen hat (z.B. `Wavelength` in µm statt `WavelengthNm`), passe Mapping + Test an die echten Namen an.

- [ ] **Step 5: Run tests to verify they pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" FdtdSMatrixToDraftConverter`
Expected: PASS (4/4).

- [ ] **Step 6: Commit**

```bash
git add CAP.Avalonia/Services/AddCustomComponent/FdtdSMatrixToDraftConverter.cs UnitTests/Components/AddCustomComponent/FdtdSMatrixToDraftConverterTests.cs
git commit -m "(+) FdtdSMatrixToDraftConverter: FDTD result / black-box / lossless-2-port -> PdkSMatrixDraft"
```

---

### Task 3: FDTD-Request aus einem Preview-Ergebnis (DRY-Refactor)

**Files:**
- Modify: `CAP.Avalonia/Services/Solvers/ComponentFdtdRequestFactory.cs`
- Test: `UnitTests/Components/AddCustomComponent/PreviewFdtdRequestTests.cs`

**Interfaces:**
- Consumes: `NazcaPreviewResult` (`IReadOnlyList<NazcaPreviewPolygon> Polygons` mit `int Layer` + `Vertices`; `IReadOnlyList<NazcaPreviewPin> Pins` mit `Name`), `FdtdSMatrixRequest`, `FdtdPolygon`, `FdtdPort`.
- Produces (neue statische Methode auf `ComponentFdtdRequestFactory`):
  - `public static FdtdSMatrixRequest BuildFromPreview(NazcaPreviewResult preview, IReadOnlyList<string> portNames, int siliconLayer = 1, double portWidthUm = 0.5)`

**Rationale:** `ComponentFdtdRequestFactory.BuildAsync` besitzt private `BuildPolygons`/`BuildPorts`. Der neue Flow hat bereits ein `NazcaPreviewResult` (aus dem Preview-Render) und darf nicht doppelt rendern. Wir heben die Polygon-/Port-Erzeugung in eine statische, wiederverwendbare Methode und lassen `BuildAsync` sie aufrufen (DRY).

- [ ] **Step 1: Read the current factory**

Lies `CAP.Avalonia/Services/Solvers/ComponentFdtdRequestFactory.cs` vollständig — insbesondere `BuildPolygons` (Layer-Filter auf `siliconLayer`) und `BuildPorts` (Index-Matching Pin-Namen). Lies `CAP_Core/Solvers/Fdtd/FdtdSMatrixRequest.cs` für die exakte Form von `FdtdPolygon`/`FdtdPort`.

- [ ] **Step 2: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class PreviewFdtdRequestTests
{
    private static NazcaPreviewResult TwoPortPreview() => new()
    {
        Success = true,
        XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double X, double Y)> { (0,0),(10,0),(10,2),(0,2) } }
        },
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 1, Angle = 180 },
            new() { Name = "o2", X = 10, Y = 1, Angle = 0 },
        }
    };

    [Fact]
    public void BuildFromPreview_keeps_layer1_polygons_and_named_ports()
    {
        var req = ComponentFdtdRequestFactory.BuildFromPreview(TwoPortPreview(), new[] { "o1", "o2" });
        req.Polygons.Count.ShouldBe(1);
        req.Ports.Count.ShouldBe(2);
    }
}
```

Passe die Konstruktion von `NazcaPreviewResult`/`NazcaPreviewPolygon`/`NazcaPreviewPin` an deren echte (evtl. `init`-only) Member an (aus Step 1).

- [ ] **Step 3: Run test to verify it fails**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" PreviewFdtdRequest`
Expected: FAIL (`BuildFromPreview` existiert nicht).

- [ ] **Step 4: Refactor — extract `BuildFromPreview`**

Verschiebe die Logik aus den privaten `BuildPolygons`/`BuildPorts` in eine neue statische `BuildFromPreview(NazcaPreviewResult preview, IReadOnlyList<string> portNames, int siliconLayer = 1, double portWidthUm = 0.5)`, die einen vollständigen `FdtdSMatrixRequest` zurückgibt (Polygone gefiltert auf `siliconLayer`, Ports index-gematcht auf `portNames`, `LayerNumber = siliconLayer`, `Is3D = false`, restliche Defaults wie bisher). Lasse die bestehende `BuildAsync` diese Methode aufrufen (mit `component.PhysicalPins.Select(p => p.Name)` als `portNames`), sodass sich das bisherige Verhalten nicht ändert. XML-Doku auf die neue public-Methode.

- [ ] **Step 5: Run the focused test AND the existing factory tests**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" PreviewFdtdRequest`
Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ComponentFdtdRequestFactory`
Expected: beide PASS (bestehendes Verhalten unverändert).

- [ ] **Step 6: Commit**

```bash
git add CAP.Avalonia/Services/Solvers/ComponentFdtdRequestFactory.cs UnitTests/Components/AddCustomComponent/PreviewFdtdRequestTests.cs
git commit -m "(~) ComponentFdtdRequestFactory: extract reusable BuildFromPreview(NazcaPreviewResult, portNames)"
```

---

### Task 4: `GeometryReference` + `ComponentGeometryExtractor`

**Files:**
- Create: `CAP.Avalonia/Services/AddCustomComponent/GeometryReference.cs`
- Create: `CAP.Avalonia/Services/AddCustomComponent/ComponentGeometryExtractor.cs`
- Test: `UnitTests/Components/AddCustomComponent/ComponentGeometryExtractorTests.cs`

**Interfaces:**
- Consumes: `NazcaComponentPreviewService.RenderAsync(string? module, string func, string? parameters, CancellationToken) : Task<NazcaPreviewResult>` und `.RenderRawCodeAsync(string code, CancellationToken) : Task<NazcaPreviewResult>`; `GdsFactoryComponentPreviewService` (Subtyp, für gdsfactory); `OverridePinMapper.BuildOverridePins(NazcaPreviewResult) : List<OverridePinData>`; `NazcaPreviewResult` (`Success`, `Error`, `XMin/YMin/XMax/YMax`, `Pins`).
- Produces:
  - `enum GeometryBackend { Nazca, GdsFactory }`
  - `record GeometryReference(GeometryBackend Backend, string? Module, string Function, string? Parameters)` mit `string ToGdsFactoryRawCode()`.
  - `record GeometryExtractResult(bool Success, string? Error, double WidthUm, double HeightUm, IReadOnlyList<OverridePinData> Pins, NazcaPreviewResult Raw)`
  - `ComponentGeometryExtractor(NazcaComponentPreviewService nazcaPreview, GdsFactoryComponentPreviewService gdsFactoryPreview)`
  - `Task<GeometryExtractResult> ExtractAsync(GeometryReference reference, CancellationToken ct = default)`

- [ ] **Step 1: Read the real signatures**

Lies `Connect-A-Pic-Core/Export/NazcaComponentPreviewService.cs` (`RenderAsync`, `RenderRawCodeAsync`, `NazcaPreviewResult`-Member) und `CAP.Avalonia/ViewModels/ComponentSettings/InstanceOverride/OverridePinMapper.cs` (`BuildOverridePins`, `OverridePinData`-Member). Prüfe, ob `RenderAsync`/`RenderRawCodeAsync` `virtual` sind (für Moq) — laut Inventur ja (`public virtual`).

- [ ] **Step 2: Write `GeometryReference` first (pure, unit-testable)**

Erzeuge `GeometryReference.cs`:

```csharp
namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>Which geometry engine renders/exports a custom component.</summary>
public enum GeometryBackend { Nazca, GdsFactory }

/// <summary>
/// A reference to a component-producing function in a Python module, e.g.
/// module "cspdk.sin300", function "coupler". Parameters is an optional Python
/// kwargs fragment (e.g. "length=10"). Renders in one of two ways depending on backend.
/// </summary>
public sealed record GeometryReference(GeometryBackend Backend, string? Module, string Function, string? Parameters)
{
    /// <summary>The fully-qualified call, e.g. "cspdk.sin300.coupler" or "coupler".</summary>
    public string QualifiedFunction => string.IsNullOrWhiteSpace(Module) ? Function : $"{Module}.{Function}";

    /// <summary>
    /// Wraps the reference in a raw-code snippet the gdsfactory preview script understands:
    /// it imports the module and assigns the built cell to a variable named `component`.
    /// </summary>
    public string ToGdsFactoryRawCode()
    {
        var import = string.IsNullOrWhiteSpace(Module) ? "import gdsfactory as gf" : $"import {Module}";
        return $"{import}\ncomponent = {QualifiedFunction}({Parameters ?? string.Empty})";
    }
}
```

- [ ] **Step 3: Write the failing extractor tests**

```csharp
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class ComponentGeometryExtractorTests
{
    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 12, YMax = 3,
        Polygons = new System.Collections.Generic.List<NazcaPreviewPolygon>(),
        Pins = new System.Collections.Generic.List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 1.5, Angle = 180 },
            new() { Name = "o2", X = 12, Y = 1.5, Angle = 0 },
        }
    };

    private static (Mock<NazcaComponentPreviewService> nazca, Mock<GdsFactoryComponentPreviewService> gds) Mocks()
    {
        // Both preview services take (pythonExecutable, scriptPath, timeout?, launchFactory?); Moq needs ctor args.
        var nazca = new Mock<NazcaComponentPreviewService>("python", "render_component_preview.py", null, null) { CallBase = false };
        var gds = new Mock<GdsFactoryComponentPreviewService>("python", "render_gdsfactory_preview.py", null, null) { CallBase = false };
        return (nazca, gds);
    }

    [Fact]
    public async Task GdsFactory_reference_renders_via_raw_code_wrapper()
    {
        var (nazca, gds) = Mocks();
        gds.Setup(g => g.RenderRawCodeAsync(It.Is<string>(s => s.Contains("cspdk.sin300.coupler")), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null));

        result.Success.ShouldBeTrue();
        result.WidthUm.ShouldBe(12);
        result.HeightUm.ShouldBe(3);
        result.Pins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Nazca_reference_renders_via_module_function()
    {
        var (nazca, gds) = Mocks();
        nazca.Setup(n => n.RenderAsync("mymod", "mycell", null, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.Nazca, "mymod", "mycell", null));

        result.Success.ShouldBeTrue();
        result.Pins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failed_render_surfaces_error_and_no_pins()
    {
        var (nazca, gds) = Mocks();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new NazcaPreviewResult { Success = false, Error = "boom" });
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var result = await extractor.ExtractAsync(
            new GeometryReference(GeometryBackend.GdsFactory, "m", "f", null));

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("boom");
    }
}
```

Falls `NazcaComponentPreviewService`/`GdsFactoryComponentPreviewService` von Moq nicht mockbar sind (nicht-virtuelle Methoden oder `sealed`): `GdsFactoryComponentPreviewService` IST `sealed` — führe stattdessen ein schmales Interface `IComponentPreviewRenderer` mit `RenderAsync`/`RenderRawCodeAsync` ein, implementiert als dünner Adapter über die zwei Services, und injiziere zwei `IComponentPreviewRenderer` in den Extractor. Passe die Tests entsprechend an (Mock<IComponentPreviewRenderer>). Bevorzuge diesen Interface-Weg, wenn Moq an den konkreten Typen scheitert.

- [ ] **Step 4: Run tests to verify they fail**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ComponentGeometryExtractor`
Expected: FAIL.

- [ ] **Step 5: Implement `ComponentGeometryExtractor`**

```csharp
using CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;
using CAP_Core.Export;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>Result of rendering a geometry reference: bounding-box size + extracted pins.</summary>
public sealed record GeometryExtractResult(
    bool Success, string? Error, double WidthUm, double HeightUm,
    IReadOnlyList<OverridePinData> Pins, NazcaPreviewResult Raw);

/// <summary>
/// Renders a <see cref="GeometryReference"/> to geometry via the appropriate preview service
/// (nazca in module mode, gdsfactory via a raw-code wrapper) and extracts the bounding-box
/// size and physical pins — the same extraction the per-instance override "Apply" performs.
/// </summary>
public sealed class ComponentGeometryExtractor
{
    private readonly NazcaComponentPreviewService _nazca;
    private readonly GdsFactoryComponentPreviewService _gdsFactory;

    /// <summary>Creates the extractor from the two shared preview services.</summary>
    public ComponentGeometryExtractor(NazcaComponentPreviewService nazcaPreview, GdsFactoryComponentPreviewService gdsFactoryPreview)
    {
        _nazca = nazcaPreview;
        _gdsFactory = gdsFactoryPreview;
    }

    /// <summary>Renders the reference and extracts size + pins. On render failure, Success is false.</summary>
    public async Task<GeometryExtractResult> ExtractAsync(GeometryReference reference, CancellationToken ct = default)
    {
        NazcaPreviewResult preview = reference.Backend == GeometryBackend.GdsFactory
            ? await _gdsFactory.RenderRawCodeAsync(reference.ToGdsFactoryRawCode(), ct)
            : await _nazca.RenderAsync(reference.Module, reference.Function, reference.Parameters, ct);

        if (!preview.Success)
            return new GeometryExtractResult(false, preview.Error, 0, 0, System.Array.Empty<OverridePinData>(), preview);

        double width = preview.XMax - preview.XMin;
        double height = preview.YMax - preview.YMin;
        var pins = OverridePinMapper.BuildOverridePins(preview);
        return new GeometryExtractResult(true, null, width, height, pins, preview);
    }
}
```

Wenn du in Step 3 den Interface-Weg gewählt hast, injiziere `IComponentPreviewRenderer nazca, IComponentPreviewRenderer gdsFactory` statt der konkreten Typen.

- [ ] **Step 6: Run tests to verify they pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" ComponentGeometryExtractor`
Expected: PASS (3/3).

- [ ] **Step 7: Commit**

```bash
git add CAP.Avalonia/Services/AddCustomComponent/GeometryReference.cs CAP.Avalonia/Services/AddCustomComponent/ComponentGeometryExtractor.cs UnitTests/Components/AddCustomComponent/ComponentGeometryExtractorTests.cs
git commit -m "(+) ComponentGeometryExtractor: render a gdsfactory/nazca function reference -> bbox + pins"
```

---

### Task 5: `NewComponentViewModel` (Orchestrierung)

**Files:**
- Create: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs`
- Test: `UnitTests/Components/AddCustomComponent/NewComponentViewModelTests.cs`

**Interfaces:**
- Consumes: `ComponentGeometryExtractor.ExtractAsync`; `IFdtdSMatrixService.CheckAvailabilityAsync`/`SolveAsync`; `ComponentFdtdRequestFactory.BuildFromPreview` (Task 3); `FdtdSMatrixConverter.ToComponentSMatrixData` (static); `FdtdSMatrixToDraftConverter` (Task 2); `UserPdkStore.Save`/`ComponentExists` (Task 1); DTOs `PdkComponentDraft`, `ProcessDefinition`, `PhysicalPinDraft`; `OverridePinData`.
- Produces:
  - `NewComponentViewModel(ComponentGeometryExtractor extractor, IFdtdSMatrixService? fdtd, UserPdkStore store, IReadOnlyList<ProcessDefinition> processes)`
  - `[ObservableProperty]` : `ComponentName`, `SelectedBackend` (GeometryBackend), `Module`, `Function`, `Parameters`, `SelectedProcess` (ProcessDefinition?), `StatusText`, `IsBusy`, `HasPreview`.
  - `[RelayCommand] RunPreview`, `[RelayCommand] ComputeSMatrix`, `[RelayCommand] Save`.
  - `PdkComponentDraft? SavedDraft { get; }`, `string? SavedProcessName { get; }`, `event EventHandler? Saved;` — damit die LeftPanel-Integration (Task 6) das Ergebnis registriert. `Func<string, string, Task<bool>>? ConfirmOverwrite` für Kollisionsdialog.

**Design notes (verhalten):**
- `RunPreview`: ruft `ExtractAsync`; bei Erfolg setzt `HasPreview=true`, merkt sich `GeometryExtractResult`.
- `ComputeSMatrix`: nur wenn `HasPreview` und `SelectedProcess != null`. Prüft `fdtd.CheckAvailabilityAsync`; baut `BuildFromPreview(raw, pins.Select(p=>p.Name))`; `SolveAsync`; bei `!Success` → `StatusText = Fehler`, KEINE S-Matrix (kein Save-taugliches Modell). Bei Erfolg → `ComponentSMatrixData` via `ToComponentSMatrixData`, gemerkt als `_computedModel`.
- `Save`: erfordert Name + Prozess + Preview. Baut `PdkComponentDraft` (Name, `GdsFactoryFunction`=QualifiedFunction wenn gdsfactory, sonst `NazcaFunction`; `WidthMicrometers`/`HeightMicrometers`; `Pins` aus `OverridePinData` → `PhysicalPinDraft`; `SMatrix` = `_computedModel==null ? BlackBox() : FromFdtd(_computedModel)`). Kollision via `ConfirmOverwrite`. Ruft `store.Save(...)`, setzt `SavedDraft`/`SavedProcessName`, feuert `Saved`.

- [ ] **Step 1: Read the real signatures**

Lies `CAP.Avalonia/Services/Solvers/FdtdSMatrixConverter.cs`, `CAP_Core/Solvers/Fdtd/IFdtdSMatrixService.cs` + `FdtdAvailability`, und `OverridePinMapper.cs` (`OverridePinData`-Member: `Name`, Offsets, Angle, `LogicalPinNumber`, `PinKind`?). Lies `PhysicalPinDraft.cs` für die Mapping-Zielfelder.

- [ ] **Step 2: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewComponentViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon> { new() { Layer = 1, Vertices = new List<(double,double)> { (0,0),(10,0),(10,2),(0,2) } } },
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
    };

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(bool withFdtd = true)
    {
        var nazca = new Mock<NazcaComponentPreviewService>("python", "s.py", null, null);
        var gds = new Mock<GdsFactoryComponentPreviewService>("python", "g.py", null, null);
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var vm = new NewComponentViewModel(extractor, withFdtd ? fdtd.Object : null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Module = "cspdk.sin300"; vm.Function = "coupler";
        vm.SelectedProcess = vm.Processes[0];
        return (vm, fdtd);
    }

    [Fact]
    public async Task Save_without_fdtd_writes_a_black_box_component()
    {
        var (vm, _) = Build(withFdtd: false);
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();      // black box, no invented physics
        vm.SavedDraft.Pins.Count.ShouldBe(2);
        vm.SavedDraft.GdsFactoryFunction.ShouldBe("cspdk.sin300.coupler");
    }

    [Fact]
    public async Task ComputeSMatrix_failure_does_not_produce_a_model()
    {
        var (vm, fdtd) = Build();
        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FdtdAvailability(true, ""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdSMatrixResult.Fail("solver blew up"));

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("solver blew up");
        vm.SavedDraft!.SMatrix.ShouldBeNull();       // failed FDTD => still no model, never fake
    }

    [Fact]
    public async Task Save_requires_a_name()
    {
        var (vm, _) = Build(withFdtd: false);
        vm.ComponentName = "   ";
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);
        vm.SavedDraft.ShouldBeNull();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
```

Passe `FdtdAvailability`-Konstruktion an die echte Form an (Step 1). Wenn Moq an den Preview-Service-Typen scheitert, verwende das in Task 4 eingeführte `IComponentPreviewRenderer` und baue den Extractor darüber.

- [ ] **Step 3: Run tests to verify they fail**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" NewComponentViewModel`
Expected: FAIL.

- [ ] **Step 4: Implement `NewComponentViewModel`**

Implementiere das ViewModel gemäß den Design-Notes. Halte es unter 250 Zeilen (lagere das Pin-Mapping `OverridePinData` → `PhysicalPinDraft` in eine private statische Hilfsmethode aus). Kernpunkte:
- `Processes` als `IReadOnlyList<ProcessDefinition>` exponieren (Dropdown-Backing).
- `RunPreview`/`ComputeSMatrix`/`Save` als `async` `[RelayCommand]`; `IsBusy` schützt vor Reentrancy.
- `ComputeSMatrix` NUR wenn `_lastPreview?.Success == true && SelectedProcess != null`.
- FDTD-Fehler: `StatusText` = `result.Error`, `_computedModel = null` — NIE eine Fake-Matrix.
- `Save`: früh raus bei leerem Namen/keinem Preview/keinem Prozess (setzt `StatusText`, lässt `SavedDraft` null).
- Kollision: wenn `store.ComponentExists(process, name)` und `ConfirmOverwrite` gesetzt → nur speichern, wenn bestätigt.

- [ ] **Step 5: Run tests to verify they pass**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" NewComponentViewModel`
Expected: PASS (3/3).

- [ ] **Step 6: Commit**

```bash
git add CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs UnitTests/Components/AddCustomComponent/NewComponentViewModelTests.cs
git commit -m "(+) NewComponentViewModel: orchestrate name/geometry/process/FDTD/save for a custom component"
```

---

### Task 6: UI-Fenster, DI-Extension und LeftPanel-Integration

**Files:**
- Create: `CAP.Avalonia/Views/NewComponentWindow.axaml` (+ `.axaml.cs`)
- Create: `CAP.Avalonia/DI/AddCustomComponentFeature.cs`
- Modify: `CAP.Avalonia/App.axaml.cs` (Aufruf der Feature-Extension)
- Modify: `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs` (`[RelayCommand] OpenNewComponent` + Registrierung)
- Modify: `CAP.Avalonia/Views/MainWindow.axaml` („+"-Button an der Komponenten-Library)
- Test: `UnitTests/Components/AddCustomComponent/LeftPanelNewComponentTests.cs`

**Interfaces:**
- Consumes: `NewComponentViewModel` (Task 5, `SavedDraft`, `SavedProcessName`, `Saved`-Event); `PdkTemplateConverter.ConvertToTemplate(PdkComponentDraft, string pdkName, string? nazcaModule, string? gdsFactoryRoutingXs = null) : ComponentTemplate`; `PdkManagerViewModel.RegisterPdk(string, string?, bool, int)`; `UserPreferencesService.AddUserPdkPath(string)`; `LeftPanelViewModel.AllTemplates`/`Categories`/`FilterComponents()`.
- Produces: `LeftPanelViewModel.RegisterSavedCustomComponent(PdkComponentDraft draft, string pdkName, string filePath)` — konvertiert Draft → Template, fügt zu `AllTemplates` + Kategorie hinzu, ruft `RegisterPdk` + `AddUserPdkPath`, `FilterComponents()`. (Öffentlich, damit testbar ohne Fenster.)

- [ ] **Step 1: Read the integration points**

Lies `LeftPanelViewModel.cs` Z324-411 (`LoadPdk`, `LoadPdkFromJsonFileAsync`, `ConvertPdkComponentToTemplate`) — spiegle exakt dieses Registrier-Muster. Lies eine bestehende DI-Extension (`CAP.Avalonia/DI/FdtdFeatureExtensions.cs`) und wie `App.axaml.cs` sie aufruft. Lies `MainWindow.axaml` an der Stelle der Komponenten-Library, um den „+"-Button zu platzieren.

- [ ] **Step 2: Write the failing test (LeftPanel-Registrierung, ohne Fenster)**

```csharp
using System.Linq;
using CAP.Avalonia.ViewModels.Panels;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class LeftPanelNewComponentTests
{
    [Fact]
    public void RegisterSavedCustomComponent_adds_template_to_the_library()
    {
        var vm = LeftPanelViewModelTestFactory.Create();  // reuse existing test construction helper
        int before = vm.AllTemplates.Count;

        var draft = new PdkComponentDraft
        {
            Name = "My Coupler", Category = "Custom",
            GdsFactoryFunction = "cspdk.sin300.coupler",
            WidthMicrometers = 10, HeightMicrometers = 2
        };
        vm.RegisterSavedCustomComponent(draft, "My CornerStone Components", "C:/tmp/x.json");

        vm.AllTemplates.Count.ShouldBe(before + 1);
        vm.AllTemplates.ShouldContain(t => t.Name == "My Coupler");
    }
}
```

Wenn es keinen Konstruktions-Helper für `LeftPanelViewModel` gibt, sieh in bestehenden `LeftPanelViewModel`-Tests nach, wie das VM dort gebaut wird, und repliziere das inline (KEINEN produktiven Test-only-Helper hinzufügen).

- [ ] **Step 3: Run test to verify it fails**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" LeftPanelNewComponent`
Expected: FAIL (`RegisterSavedCustomComponent` existiert nicht).

- [ ] **Step 4: Implement `RegisterSavedCustomComponent` in `LeftPanelViewModel`**

Spiegle `LoadPdkFromJsonFileAsync`: `var template = ConvertPdkComponentToTemplate(draft, pdkName, null);` → `AllTemplates.Add(template);` → Kategorie hinzufügen falls neu → `PdkManager.RegisterPdk(pdkName, filePath, false, 1)` (oder Anzahl aktualisieren, wenn bereits registriert — nutze `PdkManager.IsPdkLoaded(filePath)` um doppelte Registrierung zu vermeiden) → `_preferencesService.AddUserPdkPath(filePath)` → `FilterComponents()`. XML-Doku.

- [ ] **Step 5: Add `[RelayCommand] OpenNewComponent` in `LeftPanelViewModel`**

Öffnet `NewComponentWindow` mit einem `NewComponentViewModel` (Prozessliste via `GetLoadedPdkProcessEntries()`/`GetLoadedPdkDrafts()` gefiltert auf `Process != null`). Abonniert `Saved` und ruft `RegisterSavedCustomComponent(vm.SavedDraft!, ...)`. Da das Öffnen eines Fensters nicht headless-testbar ist, halte die Command-Methode dünn und delegiere die testbare Logik an `RegisterSavedCustomComponent` (in Step 4 getestet). Die Fenster-Instanziierung erfolgt über einen injizierten Dialog-Öffner oder `Func<NewComponentViewModel, Task>` (analog zu `ConfirmSaveToPdk`-Muster), damit die Command-Methode selbst frei von direkter View-Kopplung bleibt.

- [ ] **Step 6: Create `NewComponentWindow.axaml` (+ code-behind)**

Minimales Avalonia-Fenster mit `x:DataType="vm:NewComponentViewModel"`, compiled bindings: TextBox `ComponentName`; ComboBox Backend (Nazca/GdsFactory); TextBoxes `Module`/`Function`/`Parameters`; ComboBox `Processes`/`SelectedProcess`; Buttons `RunPreviewCommand`, `ComputeSMatrixCommand`, `SaveCommand`; `StatusText`-TextBlock; `IsBusy`-Indikator. Folge dem Layout-Stil von `ProcessManagementWindow.axaml`. Code-behind nur `InitializeComponent()`.

- [ ] **Step 7: Create the DI extension and wire it**

`CAP.Avalonia/DI/AddCustomComponentFeature.cs`:

```csharp
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_DataAccess.Components.AddCustomComponent;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>DI registrations for the "add custom component → user PDK" feature.</summary>
internal static class AddCustomComponentFeature
{
    /// <summary>Registers the user-PDK store and the geometry extractor.</summary>
    public static IServiceCollection AddAddCustomComponentFeature(this IServiceCollection services)
    {
        services.AddSingleton(_ => UserPdkStore.CreateDefault());
        services.AddSingleton<ComponentGeometryExtractor>();
        return services;
    }
}
```

`ComponentGeometryExtractor` braucht `NazcaComponentPreviewService` + `GdsFactoryComponentPreviewService` aus DI — prüfe, dass beide registriert sind (sie werden fürs Override-Feature registriert); falls nicht als beide Typen verfügbar, registriere sie in dieser Extension analog zur Preview-Registrierung des Override-Features. In `App.axaml.cs` neben `AddFdtdFeature()`: `services.AddAddCustomComponentFeature();`

- [ ] **Step 8: Add the "+" button to `MainWindow.axaml`**

An der Komponenten-Library (dort wo `FilteredTemplates` gelistet werden) ein kompakter Button mit Tooltip „Neue Komponente…" gebunden an `OpenNewComponentCommand` des `LeftPanelViewModel`. Folge dem vorhandenen Button-Stil des linken Panels.

- [ ] **Step 9: Build + run the focused test**

Run: `dotnet build -clp:ErrorsOnly`
Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" LeftPanelNewComponent`
Expected: Build 0 Fehler; Test PASS.

- [ ] **Step 10: Commit**

```bash
git add CAP.Avalonia/Views/NewComponentWindow.axaml CAP.Avalonia/Views/NewComponentWindow.axaml.cs CAP.Avalonia/DI/AddCustomComponentFeature.cs CAP.Avalonia/App.axaml.cs CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs CAP.Avalonia/Views/MainWindow.axaml UnitTests/Components/AddCustomComponent/LeftPanelNewComponentTests.cs
git commit -m "(+) New Component window + '+' library button, DI wiring and LeftPanel registration"
```

---

### Task 7: Architektur-Test für den Slice

**Files:**
- Modify: `UnitTests/Architecture/VerticalSliceConventionTests.cs` (Feature in die enumerierte Liste aufnehmen, falls das Muster das verlangt) ODER Create: `UnitTests/Architecture/AddCustomComponentSliceTests.cs`
- Test: derselbe.

**Interfaces:**
- Consumes: die bestehende Architektur-Test-Infrastruktur.
- Produces: eine Assertion, dass die `AddCustomComponent`-Namespaces nur erlaubte Namespaces importieren, und dass `UserPdkStore.ResolvePath` nie einen Pfad unter `CAP-DataAccess/PDKs` liefert.

- [ ] **Step 1: Read the existing architecture test**

Lies `UnitTests/Architecture/VerticalSliceConventionTests.cs` und `CrossPlatformProcessLaunchTests.cs`, um Stil/Mechanik zu übernehmen.

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

public class AddCustomComponentSliceTests
{
    [Fact]
    public void UserPdk_path_is_never_inside_the_bundled_pdk_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunima-arch-" + Guid.NewGuid().ToString("N"));
        var store = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var path = store.ResolvePath(new ProcessDefinition { Name = "CornerStone SiN" });

        path.Replace('\\', '/').ShouldNotContain("/PDKs/");
        path.ShouldStartWith(root);
    }
}
```

- [ ] **Step 3: Run to verify it fails, implement if needed, run to verify it passes**

Run: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" AddCustomComponentSlice`
(Der Test sollte nach Task 1 direkt grün sein — wenn ja, ist das die Verifikation; wenn die Vertical-Slice-Konvention eine Enumeration verlangt, ergänze den Slice dort und stelle grün.)

- [ ] **Step 4: Commit**

```bash
git add UnitTests/Architecture/AddCustomComponentSliceTests.cs
git commit -m "(+) Architecture test: user PDK never lands in the bundled PDK folder"
```

---

## Self-Review

**Spec coverage:**
- „+"-Fenster in Library → Task 6. Name/Prozess/Geometrie/FDTD/Save-Flow → Task 5. User-PDK pro Prozess, nie Foundry → Task 1 + Task 7. Ehrliche S-Matrix (FDTD/Blackbox/2-Port) → Task 2 + Task 5. Funktions-Referenz-Render (gdsfactory Rawcode-Wrapper, nazca module-mode) → Task 4. FDTD aus Preview → Task 3. DI/Registrierung → Task 6. Cross-Platform-Pfade/Kultur → Task 1. Alle Spec-Abschnitte abgedeckt.
- Rawcode-Authoring ist explizit v2 (Spec) → keine Tasks, korrekt.

**Placeholder scan:** Keine TBD/„handle edge cases" ohne Code. Alle Code-Steps enthalten echten Code oder eine präzise Read-then-mirror-Anweisung mit exakter Datei/Signatur.

**Type consistency:** `GeometryReference`/`GeometryBackend` (Task 4) konsistent in Task 5 genutzt. `GeometryExtractResult.Raw`/`Pins`/`WidthUm`/`HeightUm` (Task 4) → Task 5 Save/Compute. `BuildFromPreview` (Task 3) → Task 5 Compute. `FdtdSMatrixToDraftConverter.FromFdtd/BlackBox/LosslessTwoPort` (Task 2) → Task 5 Save. `UserPdkStore.Save/ComponentExists/ResolvePath` (Task 1) → Task 5/6/7. `RegisterSavedCustomComponent` (Task 6) konsistent mit `NewComponentViewModel.SavedDraft` (Task 5).

**Bekannte Verifikationspunkte (in den Steps als „Read the real signatures" markiert):** exakte Feldnamen von `WavelengthSMatrixEntry`, `FdtdAvailability`, `PhysicalPinDraft`, `OverridePinData`, `PdkLoader`-ctor, und Moq-Fähigkeit der Preview-Services (Fallback: `IComponentPreviewRenderer`). Diese sind bewusst dem Implementierer überlassen, weil sie 1 Read entfernt sind und exakt benannt wurden.

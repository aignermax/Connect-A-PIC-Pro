# Eigene Komponente per Rawcode (Editor + .py-Datei) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Das „Neue Komponente"-Fenster (v1, PR #702) um einen Modus „Eigener Code" erweitern: gdsfactory-/nazca-Code in einen Editor einfügen (oder aus einer `.py`-Datei laden), live rendern, S-Matrix via FDTD berechnen, und als wiederverwendbare Komponente ins eigene PDK speichern.

**Architecture:** Rawcode-Export ist bei beiden Exportern (`SimpleNazcaExporter`, `GdsFactoryExporter`) rein über die per-Instanz-Override-Map (`Identifier → NazcaCodeOverride.RawCode`+`Backend`, im .lun) getrieben. Deshalb: die PDK-Komponente speichert ihren Rawcode; beim **Platzieren** wird ein `NazcaCodeOverride` für den Instanz-Identifier geseedet → Preview UND Export laufen über den bewährten #637/#559-Pfad, **ohne Export-Umbau**. Render/Extraktion (`RenderRawCodeAsync`) und FDTD-Request (`ComponentFdtdRequestFactory.BuildFromPreview`) werden aus v1 wiederverwendet.

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit + Shouldly + Moq. Baut auf PR #702 (`*/AddCustomComponent/`) und dem #637-Override (`NazcaCodeOverride`, `NazcaComponentPreviewService.RenderRawCodeAsync`, `OverridePinMapper`).

## Global Constraints

- Keine erfundene Physik: S-Matrix nur aus echtem FDTD / Blackbox / verlustfreiem 2-Port-Ideal; FDTD-Fehler → nichts speichern.
- Kein Export-Umbau: Rawcode fließt ausschließlich über `NazcaCodeOverride.RawCode`+`Backend`, den beide Exporter schon lesen. Nicht die Exporter anfassen.
- Foundry-JSONs bleiben unangetastet; User-PDKs nur unter `%LOCALAPPDATA%/Lunima/user-pdks/`.
- Cross-Platform: `Path.Combine`, kein direkter `Process.Start`, `x:DataType` in AXAML, `InvariantCulture` für maschinennahe Strings.
- Max. 250 Zeilen pro NEUER Datei; bestehende ≤500 (hartes Limit). `LeftPanelViewModel.cs` liegt bereits bei 500 — NICHT weiter anfassen.
- XML-Doku auf public members. Nur feature-bezogene Dateien ändern.

## File Structure

- `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/PdkDraft.cs` (MODIFY) — `PdkComponentDraft.RawCode` + `RawCodeBackend`.
- `CAP.Avalonia/Services/PdkTemplateConverter.cs` (MODIFY) — Rawcode-Felder in die `ComponentTemplate` durchreichen.
- `CAP.Avalonia/ViewModels/Library/ComponentTemplates.cs` (MODIFY) — `ComponentTemplate.RawCode`/`RawCodeBackend`.
- `CAP.Avalonia/Commands/PlaceComponentCommand.cs` (MODIFY) — beim Platzieren einer Rawcode-Komponente den Override seeden.
- `CAP.Avalonia/Services/AddCustomComponent/GeometryReference.cs` (MODIFY) — Rawcode-Variante.
- `CAP.Avalonia/Services/AddCustomComponent/ComponentGeometryExtractor.cs` (MODIFY) — Rawcode direkt rendern.
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` (MODIFY) — Modus „Eigener Code": Code + LoadFromFile; Draft mit RawCode bauen.
- `CAP.Avalonia/Services/AddCustomComponent/CustomComponentDraftFactory.cs` (MODIFY) — RawCode/Backend in den Draft schreiben.
- `CAP.Avalonia/Views/NewComponentWindow.axaml` (MODIFY) — Modus-Umschalter + Code-Editor + „aus .py laden".
- Tests unter `UnitTests/Components/AddCustomComponent/`.

---

### Task 1: Rawcode-Persistenz + Template-Durchreichung

**Files:**
- Modify: `CAP-DataAccess/Components/ComponentDraftMapper/DTOs/PdkDraft.cs`
- Modify: `CAP.Avalonia/Services/PdkTemplateConverter.cs`
- Modify: `CAP.Avalonia/ViewModels/Library/ComponentTemplates.cs`
- Test: `UnitTests/Components/AddCustomComponent/RawCodePersistenceTests.cs`

**Interfaces:**
- Produces: `PdkComponentDraft.RawCode` (`string?`, JSON `rawCode`), `PdkComponentDraft.RawCodeBackend` (`string?`, JSON `rawCodeBackend`, "nazca"|"gdsfactory"); `ComponentTemplate.RawCode` (`string?`), `ComponentTemplate.RawCodeBackend` (`string?`); `PdkTemplateConverter.ConvertToTemplate` überträgt beide.

- [ ] **Step 1: Read** `DTOs/PdkDraft.cs` (`PdkComponentDraft`), `PdkTemplateConverter.ConvertToTemplate`, und `ComponentTemplates.cs` (`ComponentTemplate`-Klasse ab Z135). Nutze das vorhandene Property-Muster (JSON-Attribut-Stil).

- [ ] **Step 2: Write the failing test**

```csharp
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodePersistenceTests
{
    [Fact]
    public void PdkComponentDraft_roundtrips_rawcode_through_saver_and_loader()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunima-rawcode-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "p.json");
        var pdk = new PdkDraft
        {
            Name = "My P", Backend = "gdsfactory", Components = new()
            {
                new PdkComponentDraft
                {
                    Name = "My Cell", WidthMicrometers = 10, HeightMicrometers = 2,
                    RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()",
                    RawCodeBackend = "gdsfactory",
                    Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
                }
            }
        };
        new PdkJsonSaver().SaveToFile(pdk, path);
        var reloaded = new PdkLoader().LoadFromFileForEditing(path);
        var c = reloaded.Components[0];
        c.RawCode.ShouldContain("gf.components.straight");
        c.RawCodeBackend.ShouldBe("gdsfactory");
        System.IO.Directory.Delete(dir, true);
    }

    [Fact]
    public void ConvertToTemplate_carries_rawcode_onto_the_template()
    {
        var draft = new PdkComponentDraft
        {
            Name = "My Cell", WidthMicrometers = 10, HeightMicrometers = 2,
            RawCode = "component = gf.components.straight()", RawCodeBackend = "gdsfactory",
            Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
        };
        var template = PdkTemplateConverter.ConvertToTemplate(draft, "My P", null);
        template.RawCode.ShouldBe("component = gf.components.straight()");
        template.RawCodeBackend.ShouldBe("gdsfactory");
    }
}
```

- [ ] **Step 3: Run to verify it fails** — `py "$env:USERPROFILE\.cap-tools\smart_test.py" RawCodePersistence` → FAIL (Properties fehlen).

- [ ] **Step 4: Implement** — Auf `PdkComponentDraft` zwei Properties ergänzen (mit `[JsonPropertyName("rawCode")]`/`("rawCodeBackend")` im Stil der Nachbar-Properties). Auf `ComponentTemplate` zwei `string?`-Properties `RawCode`/`RawCodeBackend`. In `ConvertToTemplate` beide vom Draft auf das Template kopieren. XML-Doku.

- [ ] **Step 5: Run to verify it passes** — `... RawCodePersistence` → PASS (2/2).

- [ ] **Step 6: Commit** — `git commit -m "(+) PDK raw-code persistence: PdkComponentDraft.RawCode/RawCodeBackend -> ComponentTemplate"`

---

### Task 2: Override-Seeding beim Platzieren

**Files:**
- Modify: `CAP.Avalonia/Commands/PlaceComponentCommand.cs`
- Modify (falls nötig, Aufrufer): `CAP.Avalonia/ViewModels/Canvas/DesignCanvasViewModel.cs` (Übergabe des Override-Stores)
- Test: `UnitTests/Components/AddCustomComponent/RawCodePlacementSeedingTests.cs`

**Interfaces:**
- Consumes: `ComponentTemplate.RawCode`/`RawCodeBackend` (Task 1); `NazcaCodeOverride` (`RawCode`, `Backend` : `OverrideBackend`); `Component.Identifier`.
- Produces: nach dem Platzieren eines Templates mit `RawCode != null` existiert `overrides[component.Identifier]` mit passendem `RawCode`+`Backend`.

- [ ] **Step 1: Read** `CAP.Avalonia/Commands/PlaceComponentCommand.cs` (ctor + `Execute`, Z37 `CreateFromTemplate`), wie das Command konstruiert wird und wer es aufruft (`DesignCanvasViewModel`/Placement). Lies `CAP.Avalonia/Selection/NazcaOverridePropagator.cs` — falls es eine passende Seed-Methode gibt, wiederverwenden. Lies `NazcaCodeOverride` (`RawCode`, `Backend`, `OverrideBackend`-Enum).

- [ ] **Step 2: Write the failing test** (konstruiere das Command mit einem echten `Dictionary<string, NazcaCodeOverride>` als Override-Store und einem Rawcode-Template; nach `Execute` muss der Store einen Eintrag für die Instanz haben)

```csharp
using System.Collections.Generic;
using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodePlacementSeedingTests
{
    [Fact]
    public void Placing_a_rawcode_template_seeds_a_per_instance_override()
    {
        // Baue ein minimales Rawcode-Template wie in bestehenden PlaceComponentCommand-Tests;
        // siehe UnitTests, wie ComponentTemplate/Command dort konstruiert werden, und repliziere.
        var template = TestTemplateFactory.RawCodeTemplate(
            rawCode: "import gdsfactory as gf\ncomponent = gf.components.straight()",
            backend: "gdsfactory");
        var overrides = new Dictionary<string, NazcaCodeOverride>();

        var cmd = /* PlaceComponentCommand mit template, x, y, overrides-Store */ null!;
        cmd.Execute();

        overrides.Count.ShouldBe(1);
        var ovr = overrides.Values.First();
        ovr.RawCode.ShouldContain("gf.components.straight");
        ovr.Backend.ShouldBe(OverrideBackend.GdsFactory);
    }
}
```

Passe Konstruktion an die echte `PlaceComponentCommand`-Signatur an (siehe bestehende Command-Tests). Wenn es keine `TestTemplateFactory.RawCodeTemplate` gibt, baue das Template inline (setze `RawCode`/`RawCodeBackend` + minimale Pins/Maße).

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement** — `PlaceComponentCommand` einen optionalen `IDictionary<string, NazcaCodeOverride>? overrideStore` im ctor geben (die Aufrufer, v.a. `DesignCanvasViewModel`, reichen `FileOperations.StoredNazcaOverrides` durch — folge dem bestehenden Wiring). Nach `CreateFromTemplate`, wenn `template.RawCode` nicht leer ist UND der Store noch keinen Eintrag für `_component.Identifier` hat: `overrideStore[_component.Identifier] = new NazcaCodeOverride { RawCode = template.RawCode, Backend = template.RawCodeBackend == "gdsfactory" ? OverrideBackend.GdsFactory : OverrideBackend.Nazca }`. Bei `Undo` den geseedeten Eintrag wieder entfernen (Symmetrie zum bestehenden Undo). XML-Doku.
   Falls `NazcaOverridePropagator` eine passende Methode bietet, diese nutzen statt Direktzugriff.

- [ ] **Step 5: Run to verify it passes.** Zusätzlich bestehende `PlaceComponentCommand`-Tests laufen lassen (Regression): `... PlaceComponentCommand`.

- [ ] **Step 6: Commit** — `git commit -m "(+) Placement seeds a per-instance raw-code override for custom raw-code components"`

---

### Task 3: Rawcode-Modus im Extractor

**Files:**
- Modify: `CAP.Avalonia/Services/AddCustomComponent/GeometryReference.cs`
- Modify: `CAP.Avalonia/Services/AddCustomComponent/ComponentGeometryExtractor.cs`
- Test: `UnitTests/Components/AddCustomComponent/RawCodeExtractionTests.cs`

**Interfaces:**
- Consumes: `IComponentPreviewRenderer.RenderRawCodeAsync` (nazca + gdsfactory Adapter aus v1); `OverridePinMapper.BuildOverridePins`.
- Produces: eine Rawcode-Geometriequelle — entweder `GeometryReference` mit einem `RawCode`-Feld (nicht null ⇒ direkter Rawcode-Render) ODER ein neues Wertobjekt `RawCodeGeometry(GeometryBackend Backend, string Code)`. Der Extractor rendert Rawcode über den Backend-passenden `RenderRawCodeAsync(code)` und liefert dasselbe `GeometryExtractResult` wie v1.

- [ ] **Step 1: Read** `GeometryReference.cs` + `ComponentGeometryExtractor.cs` (v1). Entscheide: erweitere `GeometryReference` um `string? RawCode` (wenn gesetzt, hat es Vorrang vor `module.function`), das ist die kleinste Änderung.

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RawCodeExtractionTests
{
    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new System.Collections.Generic.List<NazcaPreviewPin>
        { new() { Name="o1", X=0, Y=0.75, Angle=180 }, new() { Name="o2", X=8, Y=0.75, Angle=0 } }
    };

    [Fact]
    public async Task RawCode_gdsfactory_renders_code_verbatim()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync("component = gf.components.mmi1x2()", It.IsAny<CancellationToken>()))
           .ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);

        var res = await extractor.ExtractAsync(GeometryReference.RawCode(GeometryBackend.GdsFactory, "component = gf.components.mmi1x2()"));

        res.Success.ShouldBeTrue();
        res.WidthUm.ShouldBe(8);
        res.Pins.Count.ShouldBe(2);
        gds.Verify(g => g.RenderRawCodeAsync("component = gf.components.mmi1x2()", It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

Passe an, falls der Extractor-Ctor konkrete Typen statt `IComponentPreviewRenderer` nimmt (v1 nutzt `IComponentPreviewRenderer` — bestätige).

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement** — Auf `GeometryReference` `string? RawCode` ergänzen + statische Factory `RawCode(GeometryBackend, string code)`. In `ComponentGeometryExtractor.ExtractAsync`: wenn `reference.RawCode` gesetzt ist, direkt `renderer.RenderRawCodeAsync(reference.RawCode, ct)` mit dem backend-passenden Renderer aufrufen (gdsfactory-Renderer für GdsFactory, nazca-Renderer für Nazca); sonst der bestehende v1-Pfad. Rest (bbox, `BuildOverridePins`, Fehlerpfad) unverändert.

- [ ] **Step 5: Run to verify it passes.** Auch v1-Extractor-Tests laufen lassen (Regression): `... ComponentGeometryExtractor`.

- [ ] **Step 6: Commit** — `git commit -m "(+) ComponentGeometryExtractor: render pasted raw code directly (both backends)"`

---

### Task 4: „Eigener Code"-Modus im NewComponentViewModel (+ .py laden)

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs`
- Modify: `CAP.Avalonia/Services/AddCustomComponent/CustomComponentDraftFactory.cs`
- Test: `UnitTests/Components/AddCustomComponent/NewComponentViewModelRawCodeTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ComponentTemplate.RawCode`), Task 3 (`GeometryReference.RawCode(...)`), v1 (`ComponentGeometryExtractor`, FDTD, `UserPdkStore`, `CustomComponentDraftFactory`).
- Produces: `NewComponentViewModel.InputMode` (enum `ReferenceMode`/`OwnCodeMode`), `Code` (string), `SelectedBackend` (im Code-Modus wieder gdsfactory ODER nazca wählbar), `LoadCodeFromFileCommand` (liest eine `.py` in `Code`); Save schreibt `RawCode`+`RawCodeBackend` in den Draft, wenn im Code-Modus. `CustomComponentDraftFactory.Build(...)` bekommt einen optionalen `rawCode`/`rawCodeBackend`-Parameter.
- Für den Dateizugriff: injiziere einen `Func<Task<string?>>? pickPyFile` (Datei-Dialog-Öffner), analog zum v1-`ConfirmOverwrite`-Muster — im Test durch ein Fake ersetzbar. KEINE direkte View-Kopplung.

- [ ] **Step 1: Read** v1 `NewComponentViewModel.cs` + `CustomComponentDraftFactory.cs` (Save-Pfad, wie `GeometryReference` gebaut wird, wie `AvailableBackends` gesetzt ist).

- [ ] **Step 2: Write the failing tests** (mocke Extractor-Renderer wie in v1; Store = echter `UserPdkStore` mit Temp-Root)

```csharp
// Test A: Im OwnCodeMode mit Code -> RunPreview nutzt GeometryReference.RawCode -> Save schreibt draft.RawCode + RawCodeBackend, GdsFactoryFunction bleibt null.
// Test B: LoadCodeFromFileCommand füllt Code aus dem injizierten pickPyFile-Fake.
// Test C: OwnCodeMode erlaubt nazca als Backend (AvailableBackends enthält im Code-Modus beide).
```

Schreibe die drei Tests mit konkreten Asserts (`vm.SavedDraft.RawCode.ShouldContain(...)`, `vm.SavedDraft.RawCodeBackend.ShouldBe("gdsfactory")`, `vm.SavedDraft.GdsFactoryFunction.ShouldBeNull()`; `vm.Code.ShouldBe("...")` nach LoadFromFile; `vm.AvailableBackends`-Inhalt je Modus).

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement** — `InputMode`-Enum + `[ObservableProperty]`; im OwnCodeMode baut `RunPreview` `GeometryReference.RawCode(SelectedBackend, Code)` statt der Referenz; `AvailableBackends` liefert im Code-Modus `{ GdsFactory, Nazca }`, im Referenz-Modus weiterhin nur `{ GdsFactory }` (v1). `LoadCodeFromFileCommand` ruft `pickPyFile()` und setzt `Code`. `Save`/`CustomComponentDraftFactory.Build` schreibt `RawCode`+`RawCodeBackend` (Backend-String) in den Draft, wenn Code-Modus (dann `GdsFactoryFunction`/`NazcaFunction` leer lassen). Halte das VM ≤250 Zeilen (ggf. Modus-spezifische Referenz-Erzeugung in eine kleine private Methode). Preview-Invalidierung (`InvalidatePreview`) auch bei `Code`/`InputMode`-Änderung auslösen.

- [ ] **Step 5: Run to verify it passes.** Auch v1 `NewComponentViewModel`-Tests (Regression).

- [ ] **Step 6: Commit** — `git commit -m "(+) NewComponentViewModel: own-code mode (paste / load .py), writes raw-code draft"`

---

### Task 5: UI — Modus-Umschalter + Code-Editor + „.py laden"

**Files:**
- Modify: `CAP.Avalonia/Views/NewComponentWindow.axaml`
- Modify: `CAP.Avalonia/Views/MainWindow.axaml.cs` (den `pickPyFile`-Dialog an das VM hängen, dort wo das Fenster geöffnet wird — via `NewComponentWindowLauncher`)
- Modify: `CAP.Avalonia/Services/AddCustomComponent/NewComponentWindowLauncher.cs` (pickPyFile-Func setzen)
- Test: manuell (headless nicht sinnvoll für Datei-Dialog); Build-/Binding-Verifikation.

**Interfaces:** Consumes Task 4 (`InputMode`, `Code`, `LoadCodeFromFileCommand`, `AvailableBackends`).

- [ ] **Step 1: Read** `NewComponentWindow.axaml` (v1-Layout) + `NewComponentWindowLauncher.cs` (wie das VM gebaut/gezeigt wird) + ein bestehendes File-Open-Dialog-Beispiel (`FileDialogService`).

- [ ] **Step 2: AXAML** — Ein `TabControl` oder `RadioButton`-Paar „Funktionsreferenz" / „Eigener Code" gebunden an `InputMode`. Im Code-Modus: mehrzeiliges `TextBox` (`AcceptsReturn=True`, monospace) gebunden an `Code` + Button „Aus .py laden…" (`LoadCodeFromFileCommand`). Backend-ComboBox bindet an `AvailableBackends`/`SelectedBackend` (zeigt im Code-Modus beide). Referenz-Felder (Module/Function/Parameters) nur im Referenz-Modus sichtbar. `x:DataType` beibehalten.

- [ ] **Step 3: Launcher/Dialog** — In `NewComponentWindowLauncher` (bzw. beim Öffnen in `MainWindow.axaml.cs`) `vm`-`pickPyFile` auf einen echten Datei-Dialog setzen (`.py`-Filter), analog zum bestehenden `FileDialogService`-Muster. Kein direkter `Process.Start`.

- [ ] **Step 4: Build** — `dotnet build -clp:ErrorsOnly` = 0 Fehler (XAML-Compile inklusive).

- [ ] **Step 5: Commit** — `git commit -m "(+) New Component window: reference/own-code mode toggle, code editor, load-.py button"`

---

### Task 6: Integrations-Test — Rawcode-Komponente end-to-end (Draft → Template → Placement-Override)

**Files:**
- Test: `UnitTests/Components/AddCustomComponent/RawCodeEndToEndTests.cs`

**Interfaces:** Consumes Tasks 1-3.

- [ ] **Step 1: Write the test** — Baue einen `PdkComponentDraft` mit `RawCode`+`RawCodeBackend`, speichere via `UserPdkStore`, lade via `PdkLoader.LoadFromFileForEditing`, konvertiere via `PdkTemplateConverter.ConvertToTemplate` → Template trägt RawCode; platziere via `PlaceComponentCommand` (mit Override-Store) → Store hat einen `NazcaCodeOverride` mit dem RawCode für die Instanz. Ein Assert pro Stufe.

- [ ] **Step 2: Run to verify it passes** (die Bausteine existieren nach Tasks 1-3). `... RawCodeEndToEnd`.

- [ ] **Step 3: Commit** — `git commit -m "(+) End-to-end test: raw-code custom component from draft to placement override"`

---

## Self-Review

- **Spec-Coverage:** Editor+Code → Task 4/5; .py laden → Task 4/5; Persistenz → Task 1; Export ohne Umbau via Placement-Seeding → Task 2 (+ End-to-End Task 6); Render/FDTD-Reuse → Task 3. nazca im Code-Modus erlaubt (Rawcode-Export deckt nazca via Override, kein Offset-Problem, weil Export den Code direkt nutzt).
- **Placeholder-Scan:** Tasks 2 & 4 verweisen für Testkonstruktion auf bestehende Command-/VM-Tests statt vollständigen Code — bewusst, weil die echten Ctor-Signaturen dort abzulesen sind; die Asserts sind konkret genannt.
- **Typkonsistenz:** `RawCode`/`RawCodeBackend` (Task 1) → Template (Task 1) → Placement-Override (Task 2) → Draft (Task 4). `GeometryReference.RawCode(...)` (Task 3) → VM (Task 4).
- **Verifikationspunkte für Implementer:** echte `PlaceComponentCommand`-ctor-Signatur + Undo-Symmetrie (Task 2); `NazcaOverridePropagator`-Wiederverwendung; `NewComponentWindowLauncher`-Dialog-Muster (Task 5).

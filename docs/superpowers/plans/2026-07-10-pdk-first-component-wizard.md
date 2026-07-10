# PDK-first „Neue Komponente"-Assistent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Das „Neue Komponente"-Fenster zu einem PDK-first-Assistenten umbauen: Komponente in ein benanntes custom PDK (bestehend oder neu) speichern; Struktur nur noch per own-code (Editor mit Syntax-Highlighting / „Aus .py laden"); Prozess an das PDK gebunden; S-Matrix via Meep-DI-Service. Function-reference-Modus entfällt.

**Architecture:** Evolution von #702/#721. `UserPdkStore` lernt benannte custom PDKs (anlegen/auflisten/anhängen). `NewComponentViewModel`/`NewComponentWindow` werden auf PDK-first umgebaut (Reference-Modus raus). Code-Editor nutzt das vorhandene `TextEditorBindingBehavior` (AvaloniaEdit+TextMate). Prozess-Editor wird über den bestehenden `MainViewModel.OpenProcessManagerCommand` gestartet. S-Matrix bleibt der DI-Service `IFdtdSMatrixService`.

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; AvaloniaEdit+TextMate (schon referenziert); xUnit+Shouldly+Moq.

## Global Constraints

- Keine erfundene Physik: S-Matrix nur aus echtem FDTD / Blackbox (null) / verlustfreiem 2-Port-Ideal; FDTD-Fehler → nichts gespeichert.
- Foundry-JSONs (`CAP-DataAccess/PDKs/*.json`) nie geschrieben; custom PDKs nur unter `%LOCALAPPDATA%/Lunima/user-pdks/`.
- S-Matrix-Berechnung bleibt der DI-Service `IFdtdSMatrixService` (kein Direktinstanziieren).
- Cross-Platform: `Path.Combine`+`SpecialFolder`; kein direkter `Process.Start`; `x:DataType`/compiled bindings; `InvariantCulture` für Slug/JSON.
- Max. 250 Zeilen/neue Datei; bestehende ≤500. XML-Doku public members. Nur feature-bezogene Dateien.

## File Structure

- `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs` (MODIFY) — benannte custom PDKs.
- `CAP-DataAccess/Components/AddCustomComponent/UserPdkInfo.cs` (CREATE) — record (Name, FilePath, Process).
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` (+ `.Save.cs`) (MODIFY) — PDK-first, Reference-Modus raus.
- `CAP.Avalonia/Views/NewComponentWindow.axaml` (MODIFY) — Sektionen, AvaloniaEdit-Editor, Help-Flyout, Header raus.
- `CAP.Avalonia/Services/AddCustomComponent/NewComponentWindowLauncher.cs` (MODIFY) — custom-PDK-Liste + OpenProcessEditor.
- `CAP.Avalonia/Views/MainWindow.axaml.cs` (MODIFY) — OpenProcessEditor + ConfirmOverwrite verdrahten.
- Tests unter `UnitTests/Components/AddCustomComponent/`.

---

### Task 1: `UserPdkStore` — benannte custom PDKs

**Files:**
- Create: `CAP-DataAccess/Components/AddCustomComponent/UserPdkInfo.cs`
- Modify: `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs`
- Test: `UnitTests/Components/AddCustomComponent/UserPdkNamedStoreTests.cs`

**Interfaces:**
- Consumes: `PdkLoader.LoadFromFileForEditing`, `PdkJsonSaver.SaveToFile`, `PdkDraft` (`Name`, `Process`, `Backend`, `GdsFactoryRoutingCrossSection`, `Components`), `ProcessDefinition` (`Name`, `Foundry`), `PdkComponentDraft` (`Name`).
- Produces:
  - `public sealed record UserPdkInfo(string Name, string FilePath, ProcessDefinition Process);`
  - `public IReadOnlyList<UserPdkInfo> ListCustomPdks()` — scannt das Root-Verzeichnis, lädt jede `*.json` edit-tolerant, überspringt unlesbare oder solche ohne `Process`; gibt (Name, FilePath, Process) zurück.
  - `public string SaveToNamedPdk(string pdkName, ProcessDefinition process, PdkComponentDraft component, string backend, string? routingCrossSection)` — Datei = `<pdkName-slug>.json`; lädt-oder-legt-an mit `PdkDraft.Name = pdkName` und `Process = process`; ersetzt/fügt Komponente per Name (OrdinalIgnoreCase); speichert; gibt Pfad zurück.
  - `public string AppendToExistingPdk(string filePath, PdkComponentDraft component)` — lädt `filePath` edit-tolerant, ersetzt/fügt Komponente, speichert, gibt Pfad zurück.
  - `public bool NamedPdkExists(string pdkName)` und `public bool ComponentExistsInFile(string filePath, string componentName)`.
- Die bestehenden `Save(process,...)`/`ResolvePath(process)`/`ComponentExists(process,...)` bleiben unverändert (Rückwärtskompatibilität für #721-Pfad/-Tests).

- [ ] **Step 1: Read** `UserPdkStore.cs` vollständig (Root-Handling, `Slug`, `NewPdk`) und `DTOs/PdkDraft.cs` (`ProcessDefinition.Foundry` vorhanden).

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

public class UserPdkNamedStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-namedpdk-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());
    private static ProcessDefinition Proc(string n) => new() { Name = n };
    private static PdkComponentDraft Comp(string n) => new()
    { Name = n, WidthMicrometers = 10, HeightMicrometers = 2,
      RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
      Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } } };

    [Fact]
    public void SaveToNamedPdk_creates_named_file_with_process_and_component()
    {
        var s = Store();
        var path = s.SaveToNamedPdk("My SiN Lib", Proc("CornerStone SiN 300"), Comp("mmi"), "gdsfactory", null);
        Path.GetFileName(path).ShouldBe("my-sin-lib.json");
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Name.ShouldBe("My SiN Lib");
        pdk.Process!.Name.ShouldBe("CornerStone SiN 300");
        pdk.Components.ShouldContain(c => c.Name == "mmi");
    }

    [Fact]
    public void ListCustomPdks_returns_named_pdks_with_their_process()
    {
        var s = Store();
        s.SaveToNamedPdk("Lib A", Proc("P1"), Comp("x"), "gdsfactory", null);
        s.SaveToNamedPdk("Lib B", Proc("P2"), Comp("y"), "gdsfactory", null);
        var list = s.ListCustomPdks();
        list.Count.ShouldBe(2);
        list.ShouldContain(i => i.Name == "Lib A" && i.Process.Name == "P1");
    }

    [Fact]
    public void AppendToExistingPdk_adds_without_duplicating()
    {
        var s = Store();
        var path = s.SaveToNamedPdk("Lib", Proc("P"), Comp("x"), "gdsfactory", null);
        s.AppendToExistingPdk(path, Comp("z"));
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Components.Count.ShouldBe(2);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
```

- [ ] **Step 3: Run to verify it fails** — `py "$env:USERPROFILE\.cap-tools\smart_test.py" UserPdkNamedStore` → FAIL.

- [ ] **Step 4: Implement** — `UserPdkInfo` record. In `UserPdkStore`: `ListCustomPdks` (Directory.Exists-guard, `Directory.GetFiles(_root,"*.json")`, jede via `_loader.LoadFromFileForEditing` in try/catch, nur mit `Process != null`, → `UserPdkInfo(pdk.Name, path, pdk.Process)`). `SaveToNamedPdk` analog zum bestehenden `Save`, aber Dateiname `Slug(pdkName)`, `PdkDraft.Name = pdkName`, `Process = process`. `AppendToExistingPdk` (lädt filePath, RemoveAll by name, Add, Save). `NamedPdkExists`/`ComponentExistsInFile`. XML-Doku.

- [ ] **Step 5: Run to verify it passes** — 3/3.

- [ ] **Step 6: Commit** — `git commit -m "(+) UserPdkStore: named custom PDKs (create/list/append)"`

---

### Task 2: `NewComponentViewModel` — PDK-first, Reference-Modus raus

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` + `NewComponentViewModel.Save.cs`
- Delete/leeren: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentInputMode.cs` (Enum wird nicht mehr gebraucht)
- Test: `UnitTests/Components/AddCustomComponent/NewComponentViewModelPdkFirstTests.cs`

**Interfaces:**
- Consumes: `UserPdkStore` (Task 1: `ListCustomPdks`, `SaveToNamedPdk`, `AppendToExistingPdk`, `NamedPdkExists`, `ComponentExistsInFile`, `UserPdkInfo`); `ComponentGeometryExtractor`/`GeometryReference.RawCode(...)`; `IFdtdSMatrixService`; `FdtdSMatrixToDraftConverter`; `CustomComponentDraftFactory.Build`.
- Produces (neue/geänderte öffentliche Oberfläche):
  - Entfernt: `InputMode`, `Module`, `Function`, `Parameters`, das per-Modus `AvailableBackends`.
  - `IReadOnlyList<UserPdkInfo> AvailableCustomPdks { get; }` (aus `store.ListCustomPdks()`).
  - `[ObservableProperty] UserPdkInfo? _selectedCustomPdk;` (null = „Neues PDK").
  - `[ObservableProperty] bool _isNewPdk;` (true → Name+Prozess-Eingabe aktiv).
  - `[ObservableProperty] string _newPdkName;`
  - `SelectedProcess` bleibt; bei bestehendem PDK ist es geerbt (aus `SelectedCustomPdk.Process`, read-only), bei „Neues PDK" wählbar aus `Processes`.
  - `[ObservableProperty] GeometryBackend _selectedBackend` (Default GdsFactory); `AvailableBackends` = `{ GdsFactory, Nazca }` (immer, da nur own-code).
  - `Code`, `LoadCodeFromFileCommand`, `RunPreviewCommand`, `ComputeSMatrixCommand`, `SaveCommand`, `HasPreview`, `IsBusy`, `StatusText`, `SavedDraft`, `SavedProcessName`, `Saved`, `ConfirmOverwrite`, `PickPyFile` bleiben.
  - `public Func<Task>? OpenProcessEditor { get; set; }` + `[RelayCommand] private async Task OpenProcessEditorCmd()` (ruft `OpenProcessEditor` falls gesetzt).
  - ctor: `(ComponentGeometryExtractor extractor, IFdtdSMatrixService? fdtd, UserPdkStore store, IReadOnlyList<ProcessDefinition> processes)` bleibt; `AvailableCustomPdks` wird im ctor via `store.ListCustomPdks()` gefüllt; `IsNewPdk` default true, wenn keine custom PDKs existieren.

**Verhalten:**
- `RunPreview`: immer `GeometryReference.RawCode(SelectedBackend, Code)` (kein Referenz-Pfad mehr).
- Effektiver Prozess: `IsNewPdk ? SelectedProcess : SelectedCustomPdk?.Process`. `OnSelectedCustomPdkChanged` setzt `IsNewPdk = value is null` und `SelectedProcess = value?.Process`.
- `CanSave => HasPreview && !IsBusy && EffectiveProcess != null && (IsNewPdk ? !string.IsNullOrWhiteSpace(NewPdkName) : true)`.
- `Save`: baut Draft (immer own-code: `CustomComponentDraftFactory.Build(name, reference, preview, sMatrix, Code, backend)`; `sMatrix = _computedModel is null ? BlackBox() : FromFdtd(_computedModel)`). Dann:
  - `IsNewPdk` → Kollision via `NamedPdkExists(NewPdkName)`+`ConfirmOverwrite`; `store.SaveToNamedPdk(NewPdkName, EffectiveProcess, draft, backend, null)`.
  - sonst → Kollision via `ComponentExistsInFile(SelectedCustomPdk.FilePath, name)`+`ConfirmOverwrite`; `store.AppendToExistingPdk(SelectedCustomPdk.FilePath, draft)`.
  - `SavedDraft`/`SavedProcessName` setzen, `Saved` feuern.
- FDTD-Fehler → `_computedModel = null`, kein Save-Abbruch nötig (Blackbox), aber nie Fake-Matrix.

- [ ] **Step 1: Read** `NewComponentViewModel.cs` + `.Save.cs` (aktueller Save-Pfad, `BuildReference`, `InvalidatePreview`, `AvailableBackends`). Identifiziere, was zum Reference-Modus gehört (entfernen).

- [ ] **Step 2: Write the failing tests** (mocke die zwei `IComponentPreviewRenderer` wie in bestehenden Tests; `UserPdkStore` mit Temp-Root)

```csharp
// Test A: keine custom PDKs -> IsNewPdk==true default; Save mit NewPdkName+SelectedProcess -> SaveToNamedPdk schreibt Datei; SavedDraft.RawCode gesetzt.
// Test B: ein bestehendes custom PDK ausgewählt -> IsNewPdk==false, SelectedProcess == pdk.Process (geerbt); Save -> AppendToExistingPdk in dessen Datei.
// Test C: CanSave false, wenn IsNewPdk && NewPdkName leer.
// Test D: FDTD-Fehler -> SavedDraft.SMatrix null (Blackbox), StatusText enthält Fehler.
// Test E: AvailableBackends enthält GdsFactory UND Nazca (immer).
```
Konkrete Asserts ausformulieren (`vm.SavedDraft.RawCode.ShouldContain(...)`, Datei-Existenz via `store.ListCustomPdks()`, `vm.SelectedProcess.ShouldBe(pdk.Process)`, `vm.SaveCommand.CanExecute(null).ShouldBeFalse()`).

- [ ] **Step 3: Run to verify it fails.**

- [ ] **Step 4: Implement** gemäß Verhalten. Reference-Felder + `InputMode` + `NewComponentInputMode.cs` entfernen (und alle Referenzen darauf, inkl. AXAML in Task 3). `InvalidatePreview()` auch bei `Code`/`SelectedBackend`/`SelectedCustomPdk`/`IsNewPdk`-Änderung. VM-Dateien ≤250 Zeilen halten (Partial-Split beibehalten).

- [ ] **Step 5: Run to verify it passes.** Bestehende `NewComponentViewModel`-Tests, die den Reference-Modus prüfen, entsprechend anpassen/entfernen (sie testen entferntes Verhalten).

- [ ] **Step 6: Commit** — `git commit -m "(~) NewComponentViewModel: PDK-first (named custom PDK select/new), drop function-reference mode"`

---

### Task 3: Fenster-Umbau (Sektionen, AvaloniaEdit, Help-Flyout) + Wiring

**Files:**
- Modify: `CAP.Avalonia/Views/NewComponentWindow.axaml`
- Modify: `CAP.Avalonia/Services/AddCustomComponent/NewComponentWindowLauncher.cs`
- Modify: `CAP.Avalonia/Views/MainWindow.axaml.cs`
- Test: manuell/Build (UI + Dialog nicht headless).

**Interfaces:** Consumes Task 2 (`AvailableCustomPdks`/`SelectedCustomPdk`/`IsNewPdk`/`NewPdkName`/`SelectedProcess`/`Processes`/`OpenProcessEditorCmd`/`Code`/`SelectedBackend`/`AvailableBackends`/`LoadCodeFromFileCommand`/`RunPreviewCommand`/`ComputeSMatrixCommand`/`SaveCommand`).

- [ ] **Step 1: Read** `NewComponentWindow.axaml` (aktuell), `ComponentSettingsDialog.axaml` (AvaloniaEdit-Muster: `xmlns:edit="using:AvaloniaEdit"`, `xmlns:behaviors="using:CAP.Avalonia.Behaviors"`, `edit:TextEditor behaviors:TextEditorBindingBehavior.BoundText="{Binding Code, Mode=TwoWay}"`), `HelpFlyoutButton.axaml` (Title/HelpContent-Einbettung), `NewComponentWindowLauncher.cs`, `MainWindow.axaml.cs` (`ShowNewComponentWindowAsync`-Lambda, `OpenProcessManagerCommand`/`ShowProcessManagerRequested`, `MessageBoxService.ShowChoicePromptAsync`-Muster).

- [ ] **Step 2: AXAML umbauen** — Header-TextBlock „New Component" + Beschreibung entfernen. Sektionen von oben:
  1. **PDK:** `ComboBox` `ItemsSource={Binding AvailableCustomPdks}` (ItemTemplate zeigt `UserPdkInfo.Name`), `SelectedItem={Binding SelectedCustomPdk}`; + „+ Neues PDK" (z.B. ein zusätzlicher Toggle/Button, der `IsNewPdk` setzt) + `TextBox` `{Binding NewPdkName}` `IsVisible={Binding IsNewPdk}`.
  2. **Prozess:** bei `IsNewPdk` ein `ComboBox` `{Binding Processes}`/`{Binding SelectedProcess}` + Button „Prozess-Editor öffnen…" (`OpenProcessEditorCmdCommand`); sonst ein read-only `TextBlock` mit `SelectedProcess.Name` (geerbt).
  3. **Name:** `TextBox` `{Binding ComponentName}`.
  4. **Struktur (Code):** `edit:TextEditor` mit `behaviors:TextEditorBindingBehavior.BoundText="{Binding Code, Mode=TwoWay}"`, `ShowLineNumbers`, monospace; Button „Aus .py laden…" (`LoadCodeFromFileCommand`); **`HelpFlyoutButton`** mit `Title` + `HelpContent` = kopierbarer Beispielcode (siehe Step 3). Backend-`ComboBox` `{Binding AvailableBackends}`/`{Binding SelectedBackend}`.
  5. **S-Matrix:** Button „Mit Meep berechnen" (`ComputeSMatrixCommand`) + `ProgressBar`/`StatusText`.
  6. **Save**-Button (`SaveCommand`).
  `x:DataType` beibehalten; `EnumToBooleanConverter`-Ressource kann entfernt werden, wenn kein Modus-Toggle mehr da ist.

- [ ] **Step 3: Help-Beispielcode** — `HelpFlyoutButton.HelpContent` mit einer kurzen Laien-Erklärung („Dein Code muss eine Variable `component` erzeugen…") und einem kopierbaren Code-Block: ein `TextBox IsReadOnly=True` (monospace) mit dem gdsfactory-Beispiel `import gdsfactory as gf\ncomponent = gf.components.mmi1x2()` + ein „Kopieren"-Button (Clipboard via `TopLevel.GetTopLevel(this).Clipboard.SetTextAsync`, oder ein kleines Command). (nazca-Beispiel als zweiter Block.)

- [ ] **Step 4: Launcher/MainWindow-Wiring** — `NewComponentWindowLauncher.BuildViewModel` unverändert nutzbar (Prozesse aus `loadedPdks`); `AvailableCustomPdks` füllt das VM selbst via `store.ListCustomPdks()`. In `MainWindow.axaml.cs` `ShowNewComponentWindowAsync`-Lambda zusätzlich setzen: `newComponentVm.OpenProcessEditor = async () => { /* denselben Pfad wie OpenProcessManagerCommand triggern, z.B. vm.OpenProcessManagerCommand.Execute(null) bzw. ShowProcessManagerRequested?.Invoke() */ };` und `newComponentVm.ConfirmOverwrite = async (name, target) => { var choice = await new MessageBoxService().ShowChoicePromptAsync($"'{name}' existiert bereits in {target}. Überschreiben?", "Überschreiben?", new[]{"Abbrechen","Überschreiben"}); return choice == 1; };`.

- [ ] **Step 5: Build** — `dotnet build -clp:ErrorsOnly` = 0 Fehler (XAML-Compile inkl. AvaloniaEdit-Namespace). Regression: `py smart_test.py NewComponentViewModel` grün.

- [ ] **Step 6: Commit** — `git commit -m "(~) New Component window: PDK-first sections, AvaloniaEdit code editor, example help flyout, no header"`

---

### Task 4: Integrations-Test + Aufräumen

**Files:**
- Test: `UnitTests/Components/AddCustomComponent/PdkFirstEndToEndTests.cs`

- [ ] **Step 1: Write the test** — (a) `store.SaveToNamedPdk(...)` → `ListCustomPdks()` enthält das PDK mit Prozess; (b) zweite Komponente via `AppendToExistingPdk` → 2 Komponenten; (c) `PdkTemplateConverter.ConvertToTemplate` einer geladenen Rawcode-Komponente trägt `RawCode`. Ein Assert pro Stufe.
- [ ] **Step 2: Run to verify it passes.** `py smart_test.py PdkFirstEndToEnd`.
- [ ] **Step 3: Grep-Check** — sicherstellen, dass keine verwaisten Referenzen auf `NewComponentInputMode`/`Module`/`Function`/`Parameters` im Feature verbleiben (`grep -rn`).
- [ ] **Step 4: Commit** — `git commit -m "(+) End-to-end test: PDK-first named custom PDK from save to list"`

---

## Self-Review

- **Spec-Coverage:** PDK-first-Layout → Task 3; nur custom PDKs + „New PDK"+Name → Task 1/2/3; Prozess geerbt vs. wählbar + „Editor öffnen" → Task 2/3; own-code + Syntax-Highlighting + „.py laden" + „?"-Beispiel → Task 3; Function-reference raus → Task 2; S-Matrix DI-Service + Ehrlichkeit → Task 2 (unverändert); benannte Persistenz → Task 1; Header raus → Task 3.
- **Placeholder:** Task 2/3 verweisen für exakte Testkonstruktion/AXAML auf bestehende Dateien (Signaturen dort abzulesen); Verhalten + Asserts sind konkret benannt.
- **Typkonsistenz:** `UserPdkInfo`/`ListCustomPdks`/`SaveToNamedPdk`/`AppendToExistingPdk` (Task 1) → VM (Task 2) → Window (Task 3). `OpenProcessEditor`-Hook (Task 2) → MainWindow-Wiring (Task 3).
- **Verifikationspunkte:** `ProcessDefinition.Foundry` vorhanden (Task 1); `TextEditorBindingBehavior.BoundText` + AvaloniaEdit-Namespace (Task 3); Prozess-Editor-Trigger-Pfad in MainWindow (Task 3); Clipboard-API in Avalonia (Task 3).

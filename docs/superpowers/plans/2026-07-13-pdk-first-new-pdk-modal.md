# PDK-first v-next: modales „New PDK" + Backend-Autoload — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** „New PDK…" als Dropdown-Eintrag → modales PDK-Erstellungsfenster (Name + Prozess inkl. Breiten, legt neue benannte User-PDK an), das das New-Component-Fenster sperrt; Backend-Beispielcode-Autoload bei leerem/unangetastetem Editor.

**Architecture:** Evolution von #723. `UserPdkStore` bekommt „leeres benanntes PDK mit Prozess anlegen". `ProcessManagementViewModel` bekommt einen Creation-Mode (PDK-Name + `CreatePdk` via Callback), der NUR neue User-PDKs anlegt und `ActiveProcess` NICHT anfasst (Umstellen bestehender Prozesse = #726, out of scope). `NewComponentViewModel` bekommt einen „New PDK…"-Sentinel im PDK-Dropdown + `CreateNewPdk`-Hook (modal) + Backend-Autoload. `MainWindow.axaml.cs` öffnet den Prozess-Editor im Creation-Mode modal (`ShowDialog`).

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit+Shouldly+Moq.

## Global Constraints

- Keine erfundene Physik: S-Matrix nur echtes FDTD / Blackbox / 2-Port-Ideal; S-Matrix bleibt DI-Service `IFdtdSMatrixService`.
- Foundry-JSONs (`CAP-DataAccess/PDKs/*.json`) nie geschrieben; custom PDKs nur unter `%LOCALAPPDATA%/Lunima/user-pdks/`.
- Der Creation-Mode fasst `FileOperations.ActiveProcess`/`SetActiveProcess` NICHT an (#726 bleibt unberührt).
- Cross-Platform: `Path.Combine`+`SpecialFolder`; kein `Process.Start`; `x:DataType`/compiled bindings; `InvariantCulture`.
- Max. 250 Zeilen/neue Datei; bestehende ≤500. `ProcessManagementViewModel` unter 500 halten (Creation-Mode ggf. in Partial `.PdkCreation.cs`). XML-Doku public members. Nur feature-bezogene Dateien.

## File Structure

- `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs` (MODIFY) — `CreateNamedPdkWithProcess`.
- `CAP.Avalonia/ViewModels/ProcessManagementViewModel.PdkCreation.cs` (CREATE, partial) — Creation-Mode.
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` (+ `.Save.cs`) (MODIFY) — Sentinel + CreateNewPdk + Backend-Autoload; inline New-PDK/Prozess raus.
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/BackendCodeExamples.cs` (CREATE) — gemeinsame Beispielcode-Konstanten.
- `CAP.Avalonia/Views/NewComponentWindow.axaml` (MODIFY) — Dropdown-Sentinel, inline-UI raus, Prozess read-only.
- `CAP.Avalonia/Views/ProcessManagementWindow.axaml` (MODIFY) — PDK-Name-Feld + „Create PDK"-Button (nur im Creation-Mode sichtbar).
- `CAP.Avalonia/Views/MainWindow.axaml.cs` (MODIFY) — `CreateNewPdk` modal wiring.
- Tests unter `UnitTests/Components/AddCustomComponent/` und `UnitTests/ViewModels/`.

---

### Task 1: `UserPdkStore.CreateNamedPdkWithProcess`

**Files:**
- Modify: `CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs`
- Test: `UnitTests/Components/AddCustomComponent/UserPdkCreateEmptyTests.cs`

**Interfaces:**
- Produces: `public string CreateNamedPdkWithProcess(string pdkName, ProcessDefinition process, string backend, string? routingCrossSection)` — schreibt `<Slug(pdkName)>.json` mit `PdkDraft { Name = pdkName, Process = process, Backend = backend, GdsFactoryRoutingCrossSection = routingCrossSection, Components = new() }`; gibt Pfad zurück. Wenn Datei existiert: überschreibt NICHT still — wirft `InvalidOperationException`, wenn `NamedPdkExists(pdkName)` (Aufrufer prüft Kollision vorher via `NamedPdkExists`). Nutzt `Directory.CreateDirectory(_root)` + `_saver.SaveToFile`.
- Consumes: bestehendes `Slug`, `_root`, `_saver`, `ResolveNamedPath`, `NamedPdkExists`, `PdkDraft`, `ProcessDefinition`.

- [ ] **Step 1: Read** `UserPdkStore.cs` (`SaveToNamedPdk`/`NewNamedPdk`/`ResolveNamedPath`/`NamedPdkExists`/`Slug`).

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkCreateEmptyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-createempty-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    [Fact]
    public void CreateNamedPdkWithProcess_writes_named_pdk_with_process_and_no_components()
    {
        var s = Store();
        var path = s.CreateNamedPdkWithProcess("My SiN Lib", new ProcessDefinition { Name = "CornerStone SiN 300" }, "gdsfactory", null);
        Path.GetFileName(path).ShouldBe("my-sin-lib.json");
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Name.ShouldBe("My SiN Lib");
        pdk.Process!.Name.ShouldBe("CornerStone SiN 300");
        pdk.Components.ShouldBeEmpty();
        s.ListCustomPdks().ShouldContain(i => i.Name == "My SiN Lib" && i.Process.Name == "CornerStone SiN 300");
    }

    [Fact]
    public void CreateNamedPdkWithProcess_throws_when_name_already_exists()
    {
        var s = Store();
        s.CreateNamedPdkWithProcess("Lib", new ProcessDefinition { Name = "P" }, "gdsfactory", null);
        Should.Throw<InvalidOperationException>(() =>
            s.CreateNamedPdkWithProcess("Lib", new ProcessDefinition { Name = "P" }, "gdsfactory", null));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
```

- [ ] **Step 3: Run to verify it fails** — `py "$env:USERPROFILE\.cap-tools\smart_test.py" UserPdkCreateEmpty` → FAIL.
- [ ] **Step 4: Implement** die Methode. XML-Doku.
- [ ] **Step 5: Run to verify it passes** (2/2) + Regression `... UserPdk`.
- [ ] **Step 6: Commit** — `git commit -m "(+) UserPdkStore: create empty named PDK with a process"` && `git push`

---

### Task 2: `ProcessManagementViewModel` — PDK-Creation-Mode

**Files:**
- Create: `CAP.Avalonia/ViewModels/ProcessManagementViewModel.PdkCreation.cs` (partial)
- Test: `UnitTests/ViewModels/ProcessManagementPdkCreationTests.cs`

**Interfaces:**
- Consumes: bestehendes `ToProcess()`, `NewProcess()`, `AvailablePresets`/`SelectedPreset`, `ProcessName`.
- Produces (auf `public partial class ProcessManagementViewModel`):
  - `[ObservableProperty] private bool _isPdkCreationMode;`
  - `[ObservableProperty] private string _pdkName = string.Empty;`
  - `public Func<string, ProcessDefinition, string>? CreateUserPdk { get; set; }` — (pdkName, process) → geschriebener Pfad (vom Aufrufer auf `UserPdkStore.CreateNamedPdkWithProcess` gesetzt).
  - `public Func<string, bool>? PdkNameExists { get; set; }` — Kollisionsprüfung (auf `UserPdkStore.NamedPdkExists`); optional.
  - `public event EventHandler<string>? PdkCreated;` — feuert mit dem Pfad nach erfolgreicher Anlage.
  - `[RelayCommand(CanExecute = nameof(CanCreatePdk))] private void CreatePdk()` — validiert Name; ruft `CreateUserPdk(PdkName, ToProcess())`; feuert `PdkCreated(path)`. `CanCreatePdk => IsPdkCreationMode && !string.IsNullOrWhiteSpace(PdkName) && CreateUserPdk != null`.
  - `public void EnterPdkCreationMode()` — setzt `IsPdkCreationMode = true`; startet mit frischem Prozess (`NewProcess()`), damit Presets wählbar/leer editierbar sind; berührt NICHT `ActiveProcess`.
  - `OnPdkNameChanged` → `CreatePdkCommand.NotifyCanExecuteChanged()`.
- **Verbindlich:** KEIN Zugriff auf `FileOperations`/`ActiveProcess`/`SetActiveProcess`. Bestehendes Verhalten (Toolbar/non-modal/`SaveProcess`) unverändert.

- [ ] **Step 1: Read** `ProcessManagementViewModel.cs` (+ `.ActiveProcess.cs`): `ToProcess()`, `NewProcess()`, `AvailablePresets`/`OnSelectedPresetChanged`/`Load`, `ProcessName`, ctor. Bestätige Zeilenzahl (Partial nötig, um ≤500 zu bleiben).

- [ ] **Step 2: Write the failing test**

```csharp
using CAP.Avalonia.Services;   // IFileDialogService fake / vorhandenes Test-Muster
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

public class ProcessManagementPdkCreationTests
{
    private static ProcessManagementViewModel Vm()
        => new(/* IFileDialogService-Fake wie in bestehenden ProcessManagement-Tests */ null!);

    [Fact]
    public void CreatePdk_invokes_callback_with_name_and_process_and_raises_event()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.PdkName = "My Lib";
        string? gotName = null; ProcessDefinition? gotProc = null; string? raised = null;
        vm.CreateUserPdk = (n, p) => { gotName = n; gotProc = p; return "C:/tmp/my-lib.json"; };
        vm.PdkCreated += (_, path) => raised = path;

        vm.CreatePdkCommand.Execute(null);

        gotName.ShouldBe("My Lib");
        gotProc.ShouldNotBeNull();
        raised.ShouldBe("C:/tmp/my-lib.json");
    }

    [Fact]
    public void CanCreatePdk_false_when_name_blank()
    {
        var vm = Vm();
        vm.EnterPdkCreationMode();
        vm.CreateUserPdk = (_, _) => "x";
        vm.PdkName = "   ";
        vm.CreatePdkCommand.CanExecute(null).ShouldBeFalse();
    }
}
```
Passe die VM-Konstruktion an das echte Test-Muster an (siehe bestehende `ProcessManagement*Tests` unter `UnitTests/`, wie `IFileDialogService` gefaked wird).

- [ ] **Step 3: Run to verify it fails.**
- [ ] **Step 4: Implement** im Partial `.PdkCreation.cs`. Keine `ActiveProcess`-Berührung.
- [ ] **Step 5: Run to verify it passes** + Regression `... ProcessManagement`. `wc -l` der VM-Dateien ≤500.
- [ ] **Step 6: Commit** — `git commit -m "(+) ProcessManagementViewModel: PDK-creation mode (name + CreatePdk, no ActiveProcess touch)"` && `git push`

---

### Task 3: Backend-Beispielcode-Konstante + Autoload

**Files:**
- Create: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/BackendCodeExamples.cs`
- Modify: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs`
- Test: `UnitTests/Components/AddCustomComponent/BackendCodeAutoloadTests.cs`

**Interfaces:**
- Produces: `public static class BackendCodeExamples { public static string For(GeometryBackend backend); public const string GdsFactory = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()"; public const string Nazca = "import nazca as nd\ncomponent = nd.Cell(name='my_component')"; }` (exakte Strings aus dem heutigen XAML übernehmen).
- Verhalten in `NewComponentViewModel`: in `OnSelectedBackendChanged(GeometryBackend value)` — wenn `string.IsNullOrWhiteSpace(Code)` ODER `Code` gleich dem Beispiel des jeweils ANDEREN Backends (unangetastetes Auto-Beispiel) → `Code = BackendCodeExamples.For(value)`. Sonst Code unangetastet lassen. (Nach dem Setzen weiterhin `InvalidatePreview()` wie bisher.)

- [ ] **Step 1: Read** `NewComponentViewModel.cs` (`OnSelectedBackendChanged`, `Code`, `InvalidatePreview`) + `NewComponentWindow.axaml` (aktuelle Beispiel-Strings in den Help-Boxen, Zeilen ~96/107).
- [ ] **Step 2: Write the failing tests**

```csharp
using CAP.Avalonia.Services.AddCustomComponent;  // GeometryBackend
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using Shouldly;
using Xunit;
// ... Build(vm) wie in bestehenden NewComponentViewModel-Tests (Extractor-Mocks + UserPdkStore-TempRoot)

// Test A: leerer Code, Backend→GdsFactory => Code == BackendCodeExamples.GdsFactory
// Test B: Code == BackendCodeExamples.GdsFactory (unangetastet), Backend→Nazca => Code == BackendCodeExamples.Nazca
// Test C: Code == "mein eigener code", Backend-Wechsel => Code UNVERÄNDERT
```
- [ ] **Step 3: Run to verify it fails.**
- [ ] **Step 4: Implement** `BackendCodeExamples` + Autoload-Logik in `OnSelectedBackendChanged`. Beim VM-Ctor: falls `Code` leer, initial das Beispiel des Default-Backends laden (damit von Anfang an ein Beispiel steht).
- [ ] **Step 5: Run to verify it passes** + Regression `... NewComponentViewModel`.
- [ ] **Step 6: Commit** — `git commit -m "(+) New Component: backend example code constant + autoload into empty/untouched editor"` && `git push`

---

### Task 4: `NewComponentViewModel` — „New PDK…"-Sentinel + Modal-Hook + Verschlankung

**Files:**
- Modify: `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs` (+ `.Save.cs`)
- Test: `UnitTests/Components/AddCustomComponent/NewComponentNewPdkSentinelTests.cs`

**Interfaces:**
- Consumes: `UserPdkInfo`, `store.ListCustomPdks()`.
- Produces:
  - Entfernt: `IsNewPdk`, `NewPdkName`, `OpenProcessEditor`/`OpenProcessEditorCmd`, inline Prozess-Wählbarkeit. (Prozess wird ausschließlich aus `SelectedCustomPdk.Process` geerbt.)
  - PDK-Dropdown-Quelle: `public IReadOnlyList<PdkChoice> PdkChoices { get; }` — Wrapper: je ein Eintrag pro `UserPdkInfo` + ein Sentinel `PdkChoice.NewPdk` (DisplayName „New PDK…"). ODER simpler: `AvailableCustomPdks` bleibt `IReadOnlyList<UserPdkInfo>` und ein separater bool-Sentinel wird über einen zusätzlichen ListenEintrag modelliert — wähle die Variante, die im AXAML-ComboBox sauber bindet (dokumentiere die Wahl).
  - `public Func<Task<UserPdkInfo?>>? CreateNewPdk { get; set; }` — vom MainWindow gesetzt (öffnet Modal, liefert neues PDK oder null).
  - Auswahl-Handler: wird der Sentinel gewählt → `await CreateNewPdk()`; bei Ergebnis `!= null` → `RefreshCustomPdks()` (neu aus `store.ListCustomPdks()`) + `SelectedCustomPdk = <neues>`; bei null → zurück auf vorherige (nicht-Sentinel-)Auswahl. Reentrancy via `IsBusy` schützen.
  - `SelectedProcess`/`EffectiveProcess` = `SelectedCustomPdk?.Process` (read-only). `CanSave => HasPreview && !IsBusy && SelectedCustomPdk != null` (kein IsNewPdk-Zweig mehr). Save-Routing: immer `AppendToExistingPdk(SelectedCustomPdk.FilePath, draft)` (das neue PDK existiert nach der Modal-Erstellung bereits).

- [ ] **Step 1: Read** `NewComponentViewModel.cs` + `.Save.cs` (aktuelle `IsNewPdk`/`NewPdkName`/`SaveToNamedPdk`-Zweige, `OnSelectedCustomPdkChanged`, ctor-Vorauswahl). Prüfe im AXAML, wie das PDK-Dropdown heute bindet.
- [ ] **Step 2: Write the failing tests**

```csharp
// Test A: PdkChoices enthält den "New PDK…"-Sentinel als letzten Eintrag.
// Test B: Sentinel gewählt + CreateNewPdk liefert ein neues UserPdkInfo => nach Auswahl ist SelectedCustomPdk das neue PDK und in AvailableCustomPdks enthalten.
// Test C: Sentinel gewählt + CreateNewPdk liefert null (Abbruch) => SelectedCustomPdk bleibt/kehrt auf die vorherige Auswahl (kein Sentinel) zurück.
// Test D: Save mit gewähltem bestehendem PDK => AppendToExistingPdk in dessen FilePath; SavedFilePath == dessen Pfad.
```
Konkrete Asserts ausformulieren; `CreateNewPdk` als Fake-Func; `UserPdkStore` mit Temp-Root (echte Dateien für ListCustomPdks).
- [ ] **Step 3: Run to verify it fails.**
- [ ] **Step 4: Implement.** `IsNewPdk`/`NewPdkName`/`OpenProcessEditor` entfernen (inkl. AXAML-Referenzen in Task 5). Sentinel-Modell + Refresh/Revert + Reentrancy. Save-Routing auf `AppendToExistingPdk` vereinfachen. VM-Dateien ≤250.
- [ ] **Step 5: Run to verify it passes** + Slice-Regression `... AddCustomComponent`.
- [ ] **Step 6: Commit** — `git commit -m "(~) New Component: 'New PDK…' dropdown sentinel + modal create hook; drop inline new-PDK/process UI"` && `git push`

---

### Task 5: Fenster — Dropdown-Sentinel, Prozess read-only, Prozess-Editor Creation-UI, Modal-Wiring

**Files:**
- Modify: `CAP.Avalonia/Views/NewComponentWindow.axaml`
- Modify: `CAP.Avalonia/Views/ProcessManagementWindow.axaml`
- Modify: `CAP.Avalonia/Views/MainWindow.axaml.cs`
- Test: Build + Regression.

**Interfaces:** Consumes Task 2 (`IsPdkCreationMode`/`PdkName`/`CreatePdkCommand`), Task 4 (`PdkChoices`/`CreateNewPdk`/`SelectedCustomPdk`).

- [ ] **Step 1: Read** `NewComponentWindow.axaml` (aktuelles PDK/Prozess-Layout), `ProcessManagementWindow.axaml` (wo ein PDK-Name-Feld + „Create PDK"-Button oben eingefügt werden, sichtbar via `IsVisible={Binding IsPdkCreationMode}`), `MainWindow.axaml.cs` (`ShowNewComponentWindowAsync`-Lambda + `ShowProcessManagerRequested`-Handler als Muster für VM-Bau/Resolver/ConfirmSaveToPdk).
- [ ] **Step 2: `NewComponentWindow.axaml`** — PDK-`ComboBox` bindet an `PdkChoices`/`SelectedPdkChoice` (bzw. das in Task 4 gewählte Modell), Sentinel „New PDK…" als Eintrag; inline „+ Neues PDK"-Textbox + inline Prozess-Picker + „Prozess-Editor öffnen…"-Button ENTFERNEN; Prozess als read-only `TextBlock` (`SelectedCustomPdk.Process.Name`). Rest unverändert.
- [ ] **Step 3: `ProcessManagementWindow.axaml`** — oben eine Sektion, sichtbar nur `IsVisible={Binding IsPdkCreationMode}`: `TextBox {Binding PdkName}` (Watermark „PDK name") + Button „Create PDK" (`CreatePdkCommand`). Bestehendes Prozess-Editor-Layout unverändert.
- [ ] **Step 4: `MainWindow.axaml.cs`** — im `ShowNewComponentWindowAsync`-Lambda `newComponentVm.CreateNewPdk = async () => {`
    - `var processVm = new ProcessManagementViewModel(new FileDialogService(this), <importers wie im bestehenden Handler>, new PdkJsonSaver());`
    - `processVm.EnterPdkCreationMode();`
    - `processVm.SetAvailablePresets(vm.LeftPanel.GetLoadedPdkDrafts());` (Presets übernehmbar)
    - `processVm.CreateUserPdk = (name, proc) => userPdkStore.CreateNamedPdkWithProcess(name, proc, "gdsfactory", null);` (userPdkStore aus DI/deps)
    - `processVm.PdkNameExists = name => userPdkStore.NamedPdkExists(name);`
    - `string? createdPath = null; processVm.PdkCreated += (_, path) => { createdPath = path; processWindow.Close(); };`
    - `var processWindow = new ProcessManagementWindow { DataContext = processVm }; await processWindow.ShowDialog(newComponentWindow);` (MODAL, Owner = New-Component-Fenster)
    - `return createdPath is null ? null : new UserPdkInfo(<Name>, createdPath, <proc>);` — ODER schlichter: `return userPdkStore.ListCustomPdks().FirstOrDefault(i => i.FilePath == createdPath);` `}`
   Setze außerdem (falls noch nicht) `newComponentVm.ConfirmOverwrite` wie in #723. Kein `Process.Start`.
- [ ] **Step 5: Build** — `dotnet build -clp:ErrorsOnly` = 0 Fehler (XAML inkl.). Regression `... NewComponentViewModel`, `... ProcessManagement`.
- [ ] **Step 6: Commit** — `git commit -m "(~) Windows: New-PDK dropdown sentinel, read-only process, modal PDK-creation editor + wiring"` && `git push`

---

### Task 6: Integrationstest + Aufräumen

**Files:**
- Test: `UnitTests/Components/AddCustomComponent/NewPdkModalFlowTests.cs`

- [ ] **Step 1: Write the test** — (a) `store.CreateNamedPdkWithProcess("Lib", proc, "gdsfactory", null)` → `ListCustomPdks()` enthält es mit leerer Komponentenliste; (b) danach `store.AppendToExistingPdk(path, comp)` → 1 Komponente; (c) `ProcessManagementViewModel`-Creation-Mode `CreatePdk` mit `CreateUserPdk = (n,p)=>store.CreateNamedPdkWithProcess(n,p,"gdsfactory",null)` → Datei existiert + `PdkCreated` gefeuert. Ein Assert pro Stufe.
- [ ] **Step 2: Run to verify it passes.**
- [ ] **Step 3: Grep-Check** — keine verwaisten Referenzen auf entfernte `IsNewPdk`/`NewPdkName`/`OpenProcessEditor` (in `CAP.Avalonia` + `NewComponentWindow.axaml`). Falls doch: entfernen.
- [ ] **Step 4: Commit** — `git commit -m "(+) End-to-end test: modal new-PDK creation + append flow"` && `git push`

---

## Self-Review

- **Spec-Coverage:** „New PDK…"-Sentinel → Task 4/5; modales Erstellungsfenster (Name+Prozess) → Task 2/5; neue User-PDK-Anlage → Task 1; Backend-Autoload → Task 3; New-Component verschlankt → Task 4/5; kein ActiveProcess-Touch (#726 getrennt) → Task 2 (Constraint). 
- **Placeholder:** Task 4 lässt die genaue Sentinel-Modellierung (Wrapper-Liste vs. Zusatz-Eintrag) offen mit klarer Anweisung „wähle, was im ComboBox sauber bindet, dokumentiere" — bewusst, da AXAML-Bindbarkeit erst am echten Control entscheidbar; Verhalten + Asserts sind konkret.
- **Typkonsistenz:** `CreateNamedPdkWithProcess` (T1) → `CreateUserPdk`-Callback (T2) → MainWindow-Wiring (T5). `CreateNewPdk`-Hook (T4) → MainWindow-Wiring (T5). `BackendCodeExamples` (T3) → VM-Autoload + XAML-Help (T5 kann XAML-Boxen auf die Konstante umstellen, optional).
- **Verifikationspunkte:** `ProcessManagementViewModel`-ctor/`ToProcess`/`NewProcess`/`SetAvailablePresets` (T2); `IFileDialogService`-Fake-Muster in bestehenden Tests (T2); ComboBox-Sentinel-Bindung (T4/T5); `ShowDialog`-Owner + Importer-Liste im MainWindow-Handler (T5).

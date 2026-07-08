# gdsfactory-Export (Issue #581) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.
> Spec: `docs/superpowers/specs/2026-07-02-gdsfactory-export-design.md` — alle
> Code-Kontrakte (Script-Aufbau, Koordinaten, Mapping) stehen dort und gelten verbatim.

**Goal:** gdsfactory als zweiter Layout-Emitter neben Nazca (Export .py + GDS),
mit Modus-Auswahl-UI (Standalone-Stubs / ubcpdk-SiEPIC) und gdsfactory-Installation
in verwaltete Environments.

**Branch:** `feat/gdsfactory-export-581` · Tests via
`$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" <Pattern>`.

## Global Constraints

- Kein `new ProcessStartInfo`/`Process.Start("` (Architektur-Test); Skript-Läufe
  über den bestehenden `GdsExportService`-Pfad (inkl. `PYTHONSAFEPATH`).
- Max 250 Zeilen pro neuer Datei; XML-Doku; `CultureInfo.InvariantCulture` für
  alle emittierten Zahlen.
- Ground-Truth-Umgebung für lokale E2E-Läufe: `%TEMP%\gf-groundtruth`
  (gdsfactory 9.34.2 + ubcpdk 3.3.4; neu erzeugbar via
  `%LOCALAPPDATA%\Lunima\tools\uv.exe venv … && uv pip install gdsfactory ubcpdk`).

### Task 1: UbcPdkCellMap + Tests
`CAP.Avalonia/Services/GdsFactoryExport/UbcPdkCellMap.cs`:
`static string? MapToUbcPdkCell(string nazcaFunction)` — identisch, wenn der Name
in der verifizierten 35er-Liste (Spec) liegt; Renames:
`ebeam_DC_2-1_te895→ebeam_DC_2m1_te895`,
`ebeam_routing_taper_te1550_w=500nm_to_w=3000nm_L=20um→…w500nm_to_w3000nm_L20um`
(analog L=40um); sonst null. TDD: exakte Treffer, Renames, Fallback-null.

### Task 2: Skript-Generator (Stub-Modus) + Tests
`GdsFactoryExporter.Export(DesignCanvasViewModel canvas, GdsFactoryExportOptions options)`
mit `record GdsFactoryExportOptions(GdsFactoryComponentMode Mode)` und
`enum GdsFactoryComponentMode { StandaloneStubs, UbcPdkCells }`.
Aufbau exakt nach Spec (Header/Stubs/Placements/Segmente/Footer);
`GdsFactoryStubWriter` und `GdsFactorySegmentWriter` als Hilfsklassen.
Wiederverwendet `NazcaCoordinateMapper` (GetCellPlacement, GetPinNazcaPosition,
GetStubAnchor, GetUnrotatedPinOffset, ToNazca, NormalizeZero).
TDD (String-Assertions am generierten Skript): Platzierungszeile
`ref_N.rotate(...)`/`.move((x, y))`, Stub-Polygon-Koordinaten, Port-Zeilen,
straight/bend-Segmente, Footer-write_gds, Gruppen-Flattening, Analysis-Tools
übersprungen.

### Task 3: ubcpdk-Modus + Fallback-Liste + Tests
Im ubcpdk-Modus `gf.get_component('<name>')` für gemappte, Stub für ungemappte
Komponenten; `GdsFactoryExporter.CollectUnmappedComponents(canvas)` liefert die
Fallback-Liste für den Dialog. Tests: gemappt vs. Fallback, PDK.activate im Header
nur in diesem Modus.

### Task 4: UI — Export-Menü + Options-Dialog
`GdsFactoryExportFormat : IExportFormat` (Muster `PhotonTorchExportFormat`),
Dialog `GdsFactoryExportDialog.axaml` + `GdsFactoryExportOptionsViewModel`
(Radio Modus, CheckBox GenerateGds, Fallback-Liste), Command
`FileOperationsViewModel.ExportGdsFactory` (Dateidialog .py → Shadowing-Guard →
Skript schreiben → optional `GdsExport.ExportScriptToGdsAsync`-Pfad).
Registrierung im Export-Menü (`MainViewModel`-Formatliste) + DI.
Tests: Options-VM-Logik, Command-Flow mit Fake-Dialogen (Muster GdsExportGuardTests).

### Task 5: Env-Manager — gdsfactory installierbar
`NazcaPackageInstaller.InstallGdsFactoryAsync` (uv pip install gdsfactory ubcpdk,
LongOperationTimeoutMs), `PythonEnvironment.GdsFactoryVersion`,
`EnvironmentHealthChecker` prüft `import gdsfactory` (Version), Env-Panel-Button
„Install gdsfactory" (selected env, IsBusy/Cancel wie Repair). Tests: VM-Guards.

### Task 6: E2E + Suite + PR
Generiertes Skript beider Modi in `%TEMP%\gf-groundtruth` ausführen → `.gds`
entsteht; Stichprobe `scripts/extract_gds_coords.py`. CI-sichere Tests skippen
ohne gdsfactory. Volle Suite, Push, PR mit Verweis auf #581-Entscheidung.

# Nazca-Environment-Integration in Settings & Export-Flow

**Datum:** 2026-07-02
**Status:** Vom Nutzer freigegeben (Chat-Review)
**Baut auf:** PR #598 (Managed Python Environment Manager, Issue #568)

## Problem

Der Python-Environment-Manager aus #598 hängt als Panel in der Properties-Sidebar,
wo er nicht hingehört. Gleichzeitig kennt die Settings-Seite „GDS Export" die
verwalteten Environments nicht: „Check Environment" prüft nur den konfigurierten
Interpreter, und wer gar kein Nazca hat, bekommt keinerlei Angebot, es installieren
zu lassen — weder in den Settings noch beim Auslösen des GDS-Exports.

## Ziele

1. Environment-Verwaltung raus aus der Properties-Sidebar, rein in ein eigenes
   Settings-Blatt.
2. Die GDS-Export-Settings zeigen nach „Check Environment" **alle** Nazca-fähigen
   Interpreter (verwaltete Environments + per Discovery gefundene System-Pythons)
   als klickbare Auswahl.
3. Fallback in den GDS-Export-Settings: kein Nazca gefunden → Angebot
   „Create + install Nazca now?".
4. Guard im Export-Flow: GDS-Export ohne verfügbares Nazca → Dialog mit
   „Install now / Open Settings / Cancel".

## Nicht-Ziele

- gdsfactory-Environments (kommt mit dem Layout-Backend aus #581; die
  Kandidatenliste wird aber paket-erweiterbar gebaut).
- Änderungen am reinen Nazca-**Skript**-Export: der funktioniert weiterhin ohne
  Python/Nazca.
- Eine zweite Fortschritts-UI: Installationen laufen immer sichtbar auf der
  „Python Environments"-Settings-Seite.

## Design

### 1. Entfernen aus der Properties-Sidebar

`PythonEnvironmentManagerPanel` wird aus `MainWindow.axaml` entfernt;
`RightPanelViewModel` verliert die `PythonEnvManager`-Property samt
Konstruktor-Parameter. Betroffene Tests (`MainViewModelTestHelper`,
`PanelWidthPersistenceTests`) werden angepasst.

### 2. Neue Settings-Seite „Python Environments"

- Neue Klasse `PythonEnvironmentsSettingsPage : ISettingsPage`
  (Titel „Python Environments", Kategorie „Export", ViewModel =
  `PythonEnvironmentManagerViewModel` aus DI).
- Registrierung in `SettingsFeatureExtensions` (eine `AddTransient`-Zeile).
- Das bestehende Panel-Markup zieht als `DataTemplate` für
  `PythonEnvironmentManagerViewModel` in `SettingsWindow.axaml` um; der
  `UserControl` `PythonEnvironmentManagerPanel` wird im Template wiederverwendet
  (kein Markup-Duplikat), nur sein Einstiegspunkt in der Sidebar entfällt.

### 3. Vereinheitlichte Interpreter-Auswahl in „GDS Export"

- `GdsExportViewModel.CheckEnvironmentAsync` füllt zusätzlich zur Statusanzeige
  eine Kandidatenliste aus zwei Quellen:
  - **Verwaltete Environments** mit Nazca (Registry-Einträge mit
    `Status == Healthy`), dargestellt mit Badge
    „Managed · Python {ver} · Nazca {ver}".
  - **System-Pythons** aus der bestehenden Discovery
    (`SearchForPythonAsync`/`AvailablePythons`-Mechanik).
- Auswahl eines Kandidaten:
  - Verwaltetes Env → `PythonEnvironmentRegistry.SetActive(name)` — wirkt durch
    die #598-Verkabelung sofort auf Preferences **und** laufende Export-Pipeline.
  - System-Python → bestehendes `SelectPython`-Verhalten.
- **Slice-Regel:** `GdsExportViewModel` importiert die Registry nicht direkt.
  Die DI-Schicht (`PythonEnvFeatureExtensions`) injiziert zwei Delegates in das
  bereits dort verdrahtete `GdsExportViewModel`:
  `Func<IReadOnlyList<ManagedEnvCandidate>>` (Kandidaten lesen) und
  `Action<string>` (Env aktivieren). `ManagedEnvCandidate` ist ein kleines
  DTO im GdsExport-Umfeld (Name, PythonExecutable, Anzeige-Text).

### 4. Fallback-Banner „Create + install Nazca now?"

- Sichtbarkeitslogik im `GdsExportViewModel`: Der letzte Check ergab
  „Nazca fehlt im aktiven Interpreter" **und** es gibt kein gesundes verwaltetes
  Env → `ShowNazcaInstallOffer == true`.
- Banner auf der GDS-Export-Seite mit Button „Create + install Nazca now".
- Klick: wechselt im Settings-Fenster auf die Seite „Python Environments"
  (SelectedPage im `SettingsWindowViewModel`) und startet dort
  `CreateAndInstall` mit Defaults (Name `nazca`, Python `3.11`), sofern kein
  gleichnamiges Env existiert. Fortschritt/Fehler erscheinen in der dortigen UI.
- Umsetzung über einen vom DI-Layer injizierten Delegate
  (`Action RequestNazcaInstall`), damit auch hier kein Direktimport nötig ist.
  Das Settings-Fenster verdrahtet den Seitenwechsel.

### 5. Export-Guard

- Einstiegspunkt: der GDS-Generierungspfad des Nazca-Exports (nur wenn
  `GenerateGdsEnabled` aktiv ist und die GDS-Generierung einen Interpreter
  braucht).
- Vor dem Start: Wenn kein Nazca-fähiger Interpreter bekannt ist (letzter
  bekannter Status bzw. schneller Registry-Blick), Dialog über den bestehenden
  `IMessageBoxService` (um eine Drei-Knopf-Variante erweitert):
  „Nazca is required to generate the GDS file." →
  **Install now** (Settings öffnen auf Env-Seite + Auto-Install starten via
  `ShowSettingsWindowAsync(typeof(PythonEnvironmentsSettingsPage))`),
  **Open Settings** (nur öffnen), **Cancel** (Export abbrechen; das Nazca-Skript
  selbst wurde ggf. schon geschrieben — das bleibt so).

### 6. Erweiterbarkeit gdsfactory

`ManagedEnvCandidate` trägt eine Paket-Beschreibung (v1: konstant „Nazca");
wenn #581 gdsfactory-Envs bringt, wird daraus eine Liste ohne UI-Umbau.

## Fehlerbehandlung

- Aktivierung eines Kandidaten, dessen Interpreter inzwischen gelöscht ist →
  bestehende Health-Check-Pfade melden Broken; die Auswahl zeigt den Fehler im
  Status, kein Crash.
- „Install now" bei bereits existierendem Env namens `nazca` → kein Duplikat;
  die Env-Seite wird geöffnet und der Nutzer sieht das bestehende Env samt
  Status (Repair steht dort zur Verfügung).
- Dialog-Abbruch → Export endet wie bisher beim Skript (kein halbes GDS).

## Tests

- `GdsExportViewModel`: Kandidaten-Aggregation (managed + discovered),
  Aktivierungs-Delegates, `ShowNazcaInstallOffer`-Logik (alle Kombinationen).
- Export-Guard: Dialog-Service gemockt; „Cancel" bricht GDS-Generierung ab,
  „Install now"/„Open Settings" rufen den Settings-Delegate auf.
- Settings-Seite: Registrierung vorhanden, Seitenwechsel-Delegate wird
  aufgerufen, Auto-Install startet nur bei nicht-existierendem Env.
- Anpassung: `RightPanelViewModel`-/Panel-Tests ohne EnvManager.

## Reihenfolge

1. #598 mergen (erledigt, `f4fdf389`).
2. Dieses Feature als eigener PR von `main`
   (Branch `feat/nazca-env-settings-integration`).

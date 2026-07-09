# gdsfactory-Export neben Nazca (Issue #581, Option 1)

**Datum:** 2026-07-02 · **Status:** Entscheidung auf #581 gepostet; autonome Umsetzung vom Nutzer beauftragt.
**Ground Truth:** gdsfactory 9.34.2 + ubcpdk 3.3.4, verifiziert per uv-Testumgebung.

## Entscheidung

Option 1 aus #581: Die `.lun`-Datei bleibt die neutrale IR; gdsfactory wird ein
**zweiter Emitter** neben dem Nazca-Exporter. Keine Nazca↔gdsfactory-Konvertierung.

## Ziele (v1 = Export)

1. **Export-Menü**: neuer Eintrag „gdsfactory (.py + GDS)" mit **Options-Dialog**:
   - Modus **Standalone**: Stub-Geometrie aus Lunima-Dimensionen/Pins (spiegelt den
     heutigen Nazca-Export; braucht nur `gdsfactory`).
   - Modus **ubcpdk (SiEPIC)**: echte ubcpdk-Zellen, wo ein Mapping existiert
     (38/42 Komponenten des SiEPIC-PDKs); Rest fällt auf Stubs zurück, der Dialog
     zeigt die Liste der Fallbacks.
   - Toggle „GDS nach Export generieren" (nutzt den bestehenden Script-Runner).
2. **Koordinatentreue**: identische Konvention wie Nazca (beide Y-up) — der
   bestehende `NazcaCoordinateMapper` liefert Platzierungen, Pin-Positionen und
   Segment-Transformationen unverändert.
3. **Waveguides**: geroutete Segmente werden 1:1 emittiert
   (`gf.components.straight`, `gf.components.bend_circular`, absolut platziert wie
   beim Nazca-Export — kein Re-Routing durch gdsfactory).
4. **Env-Integration**: gdsfactory (+ubcpdk) ist per Klick in ein verwaltetes
   Environment installierbar (Teil-Vorgriff auf #622); der generierte Lauf nutzt
   den konfigurierten Interpreter inkl. `PYTHONSAFEPATH`.

## Nicht-Ziele

- gdsfactory-**Import** (Folge-Ausbau, im Issue skizziert).
- Automatische Konvertierung bestehender Nazca-PDK-JSONs (separat, #620).
- Kalibrier-Editor für ubcpdk-Offsets (die SiEPIC-Nazca- und ubcpdk-Zellen stammen
  aus derselben EBeam-GDS-Bibliothek; die vorhandene Kalibrierung wird übernommen —
  Abweichungen sind ein Folge-Issue, nicht v1-Blocker).

## Architektur

### Script-Aufbau (gespiegelt am SimpleNazcaExporter)

```
CAP.Avalonia/Services/GdsFactoryExport/
  GdsFactoryExporter.cs        — Orchestrierung: Header, Stubs, Placements, Segmente, Footer
  GdsFactoryStubWriter.cs      — Stub-Komponenten (Rechteck + Ports) aus Lunima-Dimensionen
  GdsFactorySegmentWriter.cs   — straight/bend_circular aus PathSegments (absolut platziert)
  UbcPdkCellMap.cs             — nazcaFunction → ubcpdk-Zellname (35 exakt + 3 Renames), sonst null
```

- **Header**: `import gdsfactory as gf`; im ubcpdk-Modus zusätzlich
  `from ubcpdk import PDK; PDK.activate()`.
- **Stubs**: pro eindeutiger Komponente eine Factory
  `def stub_<name>() -> gf.Component` mit Rechteck-Polygon
  `[-ox, oy-H]..[W-ox, oy]` (Layer (1,0)) und Ports an
  `(OffsetX-ox, oy-OffsetY)` mit Orientierung `-AngleDegrees` — exakt die
  Nazca-Stub-Kontrakte, damit beide Backends dieselbe Geometrie liefern.
- **Platzierung**: `ref = c.add_ref(cell); ref.rotate(rot); ref.move((x, y))` mit
  `(x, y, rot)` aus `NazcaCoordinateMapper.GetCellPlacement` — Rotation um den
  Zellursprung, dann Translation: identisch zur `put('org', x, y, rot)`-Semantik.
- **ubcpdk-Modus**: `gf.get_component('<zellname>')` statt Stub, wenn
  `UbcPdkCellMap` einen Namen liefert; sonst Stub + Kommentar im Skript und
  Eintrag in der Fallback-Liste des Dialogs.
- **Segmente**: dieselbe Absolut-Platzierung wie der Nazca-Export
  (`straight(length=L, width=0.45)` bzw. `bend_circular(radius=R, angle=±A,
  allow_min_radius_violation=True)`, dann `rotate`/`move` auf den Startpunkt).
  Bend-Orientierung: gdsfactory-Bögen starten bei 0° und krümmen um `angle`;
  Start-Rotation = `-StartAngleDegrees` wie im Nazca-Pfad.
- **Footer**: `c.write_gds(os.path.splitext(__file__)[0] + '.gds')` — dieselbe
  Namenskonvention wie Nazca, damit `GdsExportService.ExportToGdsAsync` das GDS
  unverändert findet und öffnet.
- Raw-Nazca-Overrides (#559) haben im gf-Skript keine Entsprechung → betroffene
  Instanzen exportieren als Stub (Kommentar im Skript, Hinweis im Dialog).
- ComponentGroups werden wie im Nazca-Export geflattet (inkl. frozen paths).

### UI

- `GdsFactoryExportFormat : IExportFormat` im Export-Menü (Muster:
  `PhotonTorchExportFormat` mit `ShowOptionsDialogAsync`).
- Options-Dialog (`GdsFactoryExportDialog`): Radio Standalone/ubcpdk,
  CheckBox „Generate GDS", Info-Liste „ohne ubcpdk-Mapping: …" (live aus dem
  aktuellen Canvas berechnet), OK/Cancel.
- `FileOperationsViewModel.ExportGdsFactory`: Dateidialog (.py) → Shadowing-Guard
  (bestehend) → Skript schreiben → optional GDS-Lauf über den bestehenden
  `GdsExportService`-Pfad (inkl. `PYTHONSAFEPATH`). Fehlt `gdsfactory` im
  Interpreter, nennt die Fehlermeldung den Settings-Weg („Install gdsfactory…").

### Env-Manager

- `NazcaPackageInstaller` erhält `InstallGdsFactoryAsync(uvPath, venvPath, …)`
  (uv pip install `gdsfactory ubcpdk` — beide auf PyPI, kein Tarball nötig).
- `PythonEnvironmentManagerViewModel`: Button „Install gdsfactory" für das
  ausgewählte Environment (async, cancelbar, Health-Text zeigt gdsfactory-Version;
  `PythonEnvironment` bekommt `GdsFactoryVersion`-Feld, Health-Check prüft
  `import gdsfactory` analog pyclipper).

## Verifikation

- Unit-Tests: Skript-Erzeugung (Platzierungszeilen, Stub-Geometrie, Segment-
  Emission, ubcpdk-Mapping inkl. Renames und Fallbacks) als String-Assertions.
- E2E lokal: generiertes Skript in der Ground-Truth-uv-Umgebung ausführen →
  GDS entsteht; Koordinaten-Stichprobe via `scripts/extract_gds_coords.py`
  gegen den Nazca-Export desselben Designs.
- CI-sicher: Tests, die echtes gdsfactory brauchen, skippen ohne Installation.

## Risiken / bewusste Kompromisse

- ubcpdk-Zellgeometrie ≠ Lunima-Stub-Kalibrierung: SiEPIC-Nazca und ubcpdk teilen
  die EBeam-GDS-Fixzellen, daher wird die vorhandene Origin-Kalibrierung
  übernommen; Feinabweichungen einzelner Zellen sind Folgearbeit (Offset-Editor
  für gf wie für Nazca).
- gdsfactory-API bewegt sich schnell → generierter Code nutzt nur stabile
  Primitive (Component, add_ref, rotate/move, add_port, write_gds, straight,
  bend_circular), verifiziert gegen 9.34.2.

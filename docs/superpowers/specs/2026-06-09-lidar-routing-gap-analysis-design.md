# LiDAR-Routing-Lückenanalyse & Design

**Datum:** 2026-06-09
**Autor:** Max Aigner (+ Claude)
**Status:** Design freigegeben — Grundlage für Issues A (45°-Routing) und B (Crossing-Insertion)

---

## 1. Kontext

Ein externer, akademischer Detail-Router für photonische ICs — **LiDAR**
([ScopeX-ASU/LiDAR](https://github.com/ScopeX-ASU/LiDAR), MIT-Lizenz, Python,
ISPD 2025) — löst dasselbe Grundproblem wie unser hauseigener
Wellenleiter-Router, aber mit zwei Fähigkeiten, die uns fehlen:

1. **Nicht-Manhattan-Routing (45°-Diagonalen)** über eine *curvy-aware* A\*-Suche.
2. **Adaptive Crossing-Insertion** — Wellenleiter dürfen sich kreuzen statt nur
   auszuweichen.

Beide Tools sind komplementär: LiDAR ist ein Offline-Batch-Router für
Large-Scale-PICs; unser Router ist interaktiv (Echtzeit beim Platzieren). Ziel
dieses Dokuments ist **nicht** eine Integration von LiDAR, sondern das
**Nachbauen der zwei Kernfähigkeiten** in unserem C#-Router, orientiert an
LiDARs Algorithmus.

> Der Task-Agent hat **keinen** Zugriff auf das LiDAR-Repo. Deshalb ist der
> Algorithmus hier und in den Issues vollständig selbst-enthalten beschrieben.

**Quellen:** LiDAR-Paper (arXiv [2410.01260](https://arxiv.org/abs/2410.01260),
ISPD 2025), LiDAR 2.0 (arXiv [2505.17239](https://arxiv.org/abs/2505.17239)),
und der Quellcode `src/picroute/routing/astarsearch.py` + `drgridroute.py`.

---

## 2. Unser Router heute (Ist-Zustand)

Verzeichnis `Connect-A-Pic-Core/Routing/`:

| Datei | Rolle |
|---|---|
| `WaveguideRouter.cs` | Orchestriert: Two-Phase-A\* (200k → 2M Knoten) + Manhattan-Fallback |
| `AStarPathfinder/AStarPathfinder.cs` | A\*-Suche, **4 Richtungen** |
| `AStarPathfinder/GridDirection.cs` | Enum `East/North/West/South` + Deltas |
| `AStarPathfinder/AStarNode.cs` | Knoten `(X, Y, Direction)`, `StraightRunLength` |
| `AStarPathfinder/RoutingCostCalculator.cs` | Kosten: gerade/Turn/Proximity/PinZone; **Manhattan-Heuristik** |
| `AStarPathfinder/PathfindingGrid.cs` | `byte[,]`, Zellzustände 0=frei, 1=Komponente, 2=Wellenleiter, 3=frozen |
| `AStarPathfinder/PathSmoother.cs` | Grid-Pfad → physische Segmente; **strikte** `IsInvalidGeometry`-Ablehnung |
| `AStarPathfinder/BendBuilder.cs` | Bögen, `BendMode.Cardinal90/Flexible/Limited45` |
| `AStarPathfinder/AngleUtilities.cs` | `QuantizeToCardinal`, `DirectionToAngle` etc. |
| `ManhattanRouter.cs` | Fallback |

**Charakteristik:**
- A\*-Schritt = **genau eine Zelle** in einer der 4 Kardinalrichtungen.
- Turns nur nach `MinStraightRunCells` geraden Zellen; `PathSmoother` legt
  *nachträglich* Bögen ein und lehnt geometrisch unmögliche Pfade ab.
- Andere Wellenleiter sind **Soft-Obstacles** (Zellzustand 2 + Proximity-Cost) →
  der Router routet **drumherum**, kreuzt nie.
- Verbindungen speisen die S-Matrix über
  `WaveguideConnectionManager.GetConnectionTransfers()` mittels
  `LogicalPin.IDInFlow/IDOutFlow`.

---

## 3. LiDARs Algorithmus (verdichtet)

### 3.1 Curvy-aware A\* — der Diagonal-Trick

**Kein Hex-Grid, kein „Grid mit Diagonalen".** Das Grid bleibt fein-quadratisch
(Auflösung `s`, z. B. 0,2 µm — viel feiner als der Biegeradius `r`). Die
Diagonalen leben in **Knoten-Orientierung + parametrischer Nachbar-Generierung**:

- **Knoten = `(x, y, orientation)`**, 8 Orientierungen (0/45/…/315°).
- Zwei Zustände: **Manhattan-State (MS)** (auf Achse) und **Non-Manhattan-State
  (NMS)** (auf Diagonale).
- **Nachbarn sind radius-parametrische Makro-Sprünge**, nicht 1-Zellen-Schritte:
  - gerader Einheitsschritt (Orientierung bleibt),
  - „Bend"-Schritte, die `step₉₀ = ⌈r/s⌉` Zellen springen und um ±90° drehen,
  - 45°-Bend mit **asymmetrischen** Schritten
    `step₄₅,ₓ = ⌈(√2−1)·r/s⌉`, `step₄₅,ᵧ = ⌈(1−√2/2)·r/s⌉`
    (im Code: `r·0.415/s` bzw. `r·0.3/s`).
  - Die Asymmetrie `step₄₅,ₓ > step₄₅,ᵧ` reserviert exakt den Footprint eines
    **real fertigbaren** 45°-Bogens mit Radius `r` → keine Null-Radius-Ecke.

**Kosten** = Insertion Loss + Congestion:
```
g(n)  = α_w·WL  +  α_b·∠BN  +  α_c·#CR  +  λ_c·#grids(congestion)
```
- `α_w` Propagationsverlust (dB/µm), `α_b` Bend-Verlust, `α_c` Crossing-Verlust.
- `stepG["straight_45"] = √2·s·α_w`.

**Heuristik** (Octile-Distanz + 45°-Bend-Penalty), zulässig:
```
d_min = min(|nₓ−tₓ|, |nᵧ−tᵧ|);  d_max = max(...)
h(n)  = (d_max − d_min) + √2·d_min  +  α·IL_bend,45
        α = 1, falls d_min>0 UND d_max>0, sonst 0
```

### 3.2 Adaptive Crossing-Insertion

Trifft ein Nachbar-Kandidat während der Suche auf einen bereits gerouteten
Wellenleiter, prüft der DRC-Manager **drei Bedingungen** (`bViolateDRC`):

1. **Ausreichende gerade Länge** mit korrektem Orientierungs-State an der
   Kreuzungsstelle (Crossing braucht gerade Stubs an allen 4 Ports).
2. **Keine Blockade** — Bounding-Box der Kreuzung überlappt kein Obstacle.
3. **Port-Matching** — die 4 Crossing-Ports passen zu Querschnitt/Breite beider
   Wellenleiter; Kreuzung muss **rechtwinklig** sein (minimiert Crosstalk).

Sind alle erfüllt, wird die Kreuzung **prädiktiv während der Suche** als
Spezial-Nachbar eingefügt (`crossing_0`/`crossing_45`), mit **konstanten Kosten**
`α_c` (kein Distanz-Inkrement) und einem dekrementierten `crossing_budget`.
Doppelte Kreuzung desselben Netz-Paars → Pfad verworfen.

**Local Rip-up & Reroute (LRR):** zwei Versuche — *crossing-enabled* vs.
*crossing-disabled (NCS)* — und der mit niedrigerem Insertion Loss gewinnt.

### 3.3 Weitere LiDAR-Features (für uns NICHT in Scope)

- Congestion-aware **net ordering** (Port-Gruppen, Prioritätsscore). Wir haben
  bereits `GenerateOrderings` in `WaveguideConnectionManager` — ausreichend.
- **Port-Access-Optimierung** (propagation, spreading, channel planning) —
  Over-Engineering für unseren interaktiven Use-Case.

---

## 4. Design-Entscheidungen (freigegeben)

| Entscheidung | Wahl | Begründung |
|---|---|---|
| 45° als Default vs. Flag | **Neuer Standard (Octile-Default)** | Dichtere Layouts sind das Ziel; Kardinal wird Spezialfall von Octile. |
| Crossing-Physik | **Echte PDK-Komponente** (`ebeam_crossing4`) | Simulation-Integrity-Regel; Insertion Loss + Crosstalk müssen real in die S-Matrix. |
| Issue-Schnitt | **2 Issues**: A (45°) → B (Crossing, hängt von A ab) | A ist Fundament; B braucht orientierte Stubs + Geometrie aus A. |

### 4.1 Crossing-Komponente ist vorhanden ✅

`CAP-DataAccess/PDKs/siepic-ebeam-pdk.json` → **„Crossing 4-Port"**
(`ebeam_crossing4`), 9,7 × 9,7 µm, Ursprung mittig, voll S-Matrix-modelliert
@1550 nm:
- 4 Ports: `port 1`(180°)/`port 2`(0°) = horizontaler Durchgang;
  `port 3`(270°)/`port 4`(90°) = vertikaler Durchgang.
- Durchgang **|S₂₁| = 0,98** (≈ −0,18 dB), Rückreflexion 0,01, **Crosstalk 0,02**
  auf Querports, Phase −45°.
- Alternative: „Crossing Manhattan" (7,1 × 7,1 µm) als kompaktere Default-Variante.

> **Einschränkung:** Der PDK-Crossing ist **rein orthogonal (90°)**. Ein
> physikalisch sauberes Crossing entsteht nur, wo sich zwei Wellenleiter
> **rechtwinklig** kreuzen. Issue B beschränkt die Insertion daher auf
> 90°-Schnitte (ein `crossing_45` bräuchte einen eigenen PDK-Eintrag).

---

## 5. Mapping LiDAR → unser Code

| LiDAR-Konzept | Unser Einstiegspunkt | Änderung |
|---|---|---|
| 8 Orientierungen | `GridDirection.cs` | Enum um `NorthEast/NorthWest/SouthEast/SouthWest` erweitern; Deltas, `FromAngle`, `GetOpposite`, `GetTurnAngle`, `GetAngleDegrees` anpassen |
| Knoten-State | `AStarNode.cs` | trägt `Direction` schon — 8 Werte genügen |
| Parametr. Nachbarn | `AStarPathfinder.GetNeighbors` | Diagonal-Nachbarn + Diagonal-Blockcheck (kein Eck-Schneiden) |
| Octile-Heuristik | `RoutingCostCalculator.CalculateHeuristic` | Manhattan → Octile; Diagonalkosten `√2·s` |
| 45°-Bögen | `PathSmoother` + `BendBuilder` | `BendMode.Diagonal45`; Smoother muss 45°-Korner emittieren |
| Cardinal-Quantisierung | `AngleUtilities.QuantizeToCardinal` | neue `QuantizeTo45` (8 Sektoren) |
| Crossing-Detection | `WaveguideConnectionManager` / neue `CrossingInserter` | Schnittpunkt-Erkennung gegen Zellzustand 2 |
| Crossing als Komponente | `ebeam_crossing4` instanziieren + 4 Sub-Verbindungen | speist S-Matrix automatisch über `GetConnectionTransfers` |

---

## 6. Risiken

- **PathSmoother-Regression:** Strikte Geometrie-Ablehnung — wenn Octile-Pfade
  Bögen erzeugen, die der Smoother nicht fitten kann, schlägt Routing fehl.
  Mitigation: LiDARs parametrische Schrittweiten *während* der Suche (garantiert
  Fitbarkeit), nicht erst im Smoother.
- **Bestehende Routing-Tests** (`UnitTests/Routing/*`) erwarten Kardinal-Pfade.
  Müssen auf Octile angepasst statt gelöscht werden.
- **Crossing & S-Matrix:** Sub-Verbindungen dürfen die Netz-Identität nicht
  brechen — Lichtpfad muss durchgehend bleiben.

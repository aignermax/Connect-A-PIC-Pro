#!/usr/bin/env python3
"""Generates examples/Logic Gate RAM 2x2.lun (issue #1142).

NAND2TETRIS 'RAM' slice (rung 5): 2 words x 2 bits, one address bit. Composed
only from the shipped gate templates of 'Logic Gate Register 2-bit.lun'
(NOT / NAND / COPY shapes):

- Write path: DMUX of LOAD by ADDR (the AND/NOT patterns of the MUX example
  #1059) -> per word EN = NAND(select, LOAD) (the inverted load enable) and
  IW = NOT(EN) (the true load enable) -> each word is the shipped 2-bit
  Register pattern (#1138): hold arm H = NAND(R, EN), load arm
  LE = NAND(D, IW), register REG = NAND(H, LE), plus a read-tap copy feeding
  the second behavior's read arm — one waveguide per driven signal. Fan-outs
  of select/enable levels are served by copy cascades (the register's CPNL
  pattern), never by duplicating a driver onto two wires (load honesty,
  #1109).
- Read path: 2-to-1 MUX per data bit as in #1059; select arms
  MA = NAND(word 0, NA) / MB = NAND(word 1, ADDR) fed from the stored copy
  outputs, OR-combined by OUT = NAND(MA, MB).

Every group/component/pin identifier receives a fresh GUID; all identifier
uniqueness is asserted before writing (duplicate-identifier defect, #1049).

Wire map (47 wires, 37 groups):
  ADDR tree: CPAX(Y1->EN1.B, Y2->CPA.A); CPA(Y1->MB0.B, Y2->MB1.B)
  NA tree:   NOTA.Y->CPNX.A; CPNX(Y1->EN0.B, Y2->CPN.A); CPN(Y1->MA0.B, Y2->MA1.B)
  per word:  EN{w}.Y->CPEA{w}.A; CPEA{w}(Y1->IW{w}.A, Y2->CPEB{w}.A);
             CPEB{w}(Y1->H{w}0.B, Y2->H{w}1.B); IW{w}.Y->CPI{w}.A;
             CPI{w}(Y1->LE{w}0.B, Y2->LE{w}1.B)
  per bit:   LE->REG.B, H->REG.A, REG.Y->CP.A, CP.Y1->H.A (feedback),
             CP0i.Y2->MAi.A, CP1i.Y2->MBi.A (read), MA/MB->OUT
"""
import json
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SOURCE = REPO / "examples" / "Logic Gate Register 2-bit.lun"
TARGET = REPO / "examples" / "Logic Gate RAM 2x2.lun"

with SOURCE.open(encoding="utf-8") as f:
    SRC = json.load(f)

_templates = {}
for grp in SRC["Groups"]:
    name = grp["GroupDto"]["GroupName"]
    shape = "COPY" if name == "CPNL" else ("NOT" if name == "NOTL" else "NAND")
    _templates[shape] = grp

LOGIC_NOTE = ("The whole RAM composes at the logic layer, not optically: every gate's "
              "truth table is extracted once in isolation, so each stage restores clean "
              "0/1 levels by construction \u2014 there is deliberately no multi-stage passive "
              "optical cascade here.")

_USED = set()


def use(val):
    if val in _USED:
        raise SystemExit(f"duplicate identifier generated: {val}")
    _USED.add(val)
    return val


def instantiate(shape, new_name, description, canvas, input_signals=None,
                output_signals=None, is_register=None):
    tmpl = _templates[shape]
    gd = json.loads(json.dumps(tmpl["GroupDto"]))
    tt = json.loads(json.dumps(tmpl["TruthTablePinAssignment"]))
    guid_map = {old: use(str(uuid.uuid4())) for old in gd["ChildComponentGuids"]}
    id_map = {old: use(f"{old.split('_', 1)[0]}_{uuid.uuid4().hex}") for old in gd["ChildComponentIds"]}

    gd["Identifier"] = use(f"group_{uuid.uuid4().hex}")
    gd["GroupName"] = new_name
    gd["Description"] = description
    gd["IdGuid"] = use(str(uuid.uuid4()))
    gd["ChildComponentGuids"] = [guid_map[g] for g in gd["ChildComponentGuids"]]
    gd["ChildComponentIds"] = [id_map[c] for c in gd["ChildComponentIds"]]
    for path in gd["InternalPaths"]:
        path["PathId"] = use(str(uuid.uuid4()))
        path["StartComponentId"] = id_map[path["StartComponentId"]]
        if path["StartComponentGuid"] in guid_map:
            path["StartComponentGuid"] = guid_map[path["StartComponentGuid"]]
        path["EndComponentId"] = id_map[path["EndComponentId"]]
        if path["EndComponentGuid"] in guid_map:
            path["EndComponentGuid"] = guid_map[path["EndComponentGuid"]]
    for pin in gd["ExternalPins"]:
        pin["PinId"] = use(str(uuid.uuid4()))
        pin["InternalComponentId"] = id_map[pin["InternalComponentId"]]
        if pin["InternalComponentGuid"] in guid_map:
            pin["InternalComponentGuid"] = guid_map[pin["InternalComponentGuid"]]

    children = []
    for child in tmpl["ChildComponents"]:
        c = json.loads(json.dumps(child))
        c["Identifier"] = id_map[c["Identifier"]]
        c["ComponentGuid"] = guid_map[c["ComponentGuid"]]
        children.append(c)

    if input_signals is None:
        tt.pop("InputSignalNames", None)
    else:
        tt["InputSignalNames"] = input_signals
    if output_signals is None:
        tt.pop("OutputSignalNames", None)
    else:
        tt["OutputSignalNames"] = output_signals
    if is_register is not None:
        tt["IsRegister"] = is_register

    return {"GroupDto": gd, "ChildComponents": children, "CanvasX": canvas[0],
            "CanvasY": canvas[1], "TruthTablePinAssignment": tt}


GROUPS = []
WIRES = []


def emit(shape, name, canvas, desc, **kw):
    GROUPS.append(instantiate(shape, name, desc + " " + LOGIC_NOTE, canvas, **kw))
    return name


def wire(start, start_pin, end, end_pin):
    WIRES.append((start, start_pin, end, end_pin))


# ---- Address/NA fan-out trees (one waveguide per driven signal) ----
emit("COPY", "CPAX", (100, 100),
     "copy gate (input A, threshold 0.375): ADDR fan-out onto the word-1 DMUX enable "
     "EN1 and the read-path selection cascade CPA \u2014 one waveguide per signal "
     "(#1128/#1138).",
     input_signals={"A": "ADDR"})
emit("COPY", "CPA", (500, 100),
     "copy gate (input A, threshold 0.375): the second ADDR arm onto the two read-path "
     "word-1 select gates MB0/MB1.",
     input_signals=None)
wire("CPAX", "Y1", "EN1", "B")
wire("CPAX", "Y2", "CPA", "A")
wire("CPA", "Y1", "MB0", "B")
wire("CPA", "Y2", "MB1", "B")

emit("NOT", "NOTA", (100, 300),
     "NOT reading (input A, BIAS constantly on, threshold 0.375) of the shipped "
     "'Logic Gate NOT-NAND' gate. Role in the RAM: ADDR inversion, NA = NOT(ADDR), "
     "feeding the word-0 DMUX enable EN0 and the read-path word-0 select cascade CPN.",
     input_signals={"A": "ADDR"})
emit("COPY", "CPNX", (500, 300),
     "copy gate (input A, threshold 0.375): the inverted-address fan-out onto the "
     "word-0 enable EN0 and the word-0 read cascade CPN.",
     input_signals=None)
emit("COPY", "CPN", (900, 300),
     "copy gate (input A, threshold 0.375): the second NA arm onto the two read-path "
     "word-0 select gates MA0/MA1.",
     input_signals=None)
wire("NOTA", "Y", "CPNX", "A")
wire("CPNX", "Y1", "EN0", "B")
wire("CPNX", "Y2", "CPN", "A")
wire("CPN", "Y1", "MA0", "B")
wire("CPN", "Y2", "MA1", "B")

# ---- Write path per word: EN = NAND(select, LOAD), IW = NOT(EN) ----
for w in (0, 1):
    sel = "ADDR" if w == 1 else "NA = NOT(ADDR)"
    emit("NAND", f"EN{w}", (900, 100 + 800 * w),
         f"DMUX gate of word {w} (inputs A/B, threshold 0.125 \u2014 the AND/NOT pattern "
         f"behind the MUX example #1059): EN = NAND({sel}, LOAD), the inverted word-{w} "
         f"load enable.",
         input_signals={"A": "LOAD"}, is_register=False)
    emit("COPY", f"CPEA{w}", (1300, 100 + 800 * w),
         "copy gate (input A, threshold 0.375): the inverted-enable fan-out onto the "
         "inverter IW and the hold-arm chain CPEB.",
         input_signals=None)
    emit("COPY", f"CPEB{w}", (1700, 100 + 800 * w),
         "copy gate (input A, threshold 0.375): the inverted enable onto the two hold "
         "arms H{w}0/H{w}1.",
         input_signals=None)
    emit("NOT", f"IW{w}", (1300, 300 + 800 * w),
         "inverter (input A, threshold 0.375): the true word load enable feeding the "
         "load arms through CPI.",
         input_signals=None)
    emit("COPY", f"CPI{w}", (1700, 300 + 800 * w),
         "copy gate (input A, threshold 0.375): the true load enable onto the two load "
         "arms LE{w}0/LE{w}1.",
         input_signals=None)
    wire(f"EN{w}", "Y", f"CPEA{w}", "A")
    wire(f"CPEA{w}", "Y1", f"IW{w}", "A")
    wire(f"CPEA{w}", "Y2", f"CPEB{w}", "A")
    wire(f"IW{w}", "Y", f"CPI{w}", "A")
    for i in (0, 1):
        y = 500 + 800 * w + 400 * i
        emit("NAND", f"H{w}{i}", (1100, y),
             f"hold arm of bit D{i}, word {w} (inputs A/B, threshold 0.125): "
             f"H = NAND(R{i}, EN{w}) \u2014 the shipped register's hold pattern (#1138), "
             f"blocked while this word loads.",
             input_signals=None, is_register=False)
        emit("NAND", f"LE{w}{i}", (700, y),
             f"load arm of bit D{i}, word {w} (inputs A/B, threshold 0.125): "
             f"LE = NAND(D{i}, IW{w}) \u2014 passes D{i} while this word is addressed "
             f"under LOAD.",
             input_signals={"A": f"D{i}"}, is_register=False)
        emit("NAND", f"REG{w}{i}", (1500, y),
             f"register of bit D{i}, word {w} (inputs A/B, threshold 0.125): "
             f"REG = NAND(H, LE) closing the bit multiplexer; register designation as in "
             f"the shipped register (#1098).",
             is_register=True)
        emit("COPY", f"CP{w}{i}", (1900, y),
             f"read tap of stored word {w} bit D{i} (input A, threshold 0.375): one arm "
             f"feeds the register's own hold feedback, the other the read MUX arm \u2014 "
             f"one waveguide per signal; the word-{w} bit tap W{w}D{i} is renamed on the "
             f"read arm.",
             output_signals={"Y2": f"W{w}D{i}"}, is_register=False)
        wire(f"CPEB{w}", f"Y{i + 1}", f"H{w}{i}", "B")
        wire(f"CPI{w}", f"Y{i + 1}", f"LE{w}{i}", "B")
        wire(f"H{w}{i}", "Y", f"REG{w}{i}", "A")
        wire(f"LE{w}{i}", "Y", f"REG{w}{i}", "B")
        wire(f"REG{w}{i}", "Y", f"CP{w}{i}", "A")
        wire(f"CP{w}{i}", "Y1", f"H{w}{i}", "A")

# ---- Read path: 2-to-1 MUX per data bit (#1059) ----
for i in (0, 1):
    emit("NAND", f"MA{i}", (2300, 500 + 400 * i),
         f"word-0 select arm of the read MUX, data bit {i} (inputs A/B, threshold "
         f"0.125): MA = NAND(W0D{i}, NA) \u2014 passes word 0 while ADDR reads low.",
         input_signals=None, is_register=False)
    emit("NAND", f"MB{i}", (2300, 900 + 400 * i),
         f"word-1 select arm of the read MUX, data bit {i} (inputs A/B, threshold "
         f"0.125): MB = NAND(W1D{i}, ADDR) \u2014 passes word 1 while ADDR reads high.",
         input_signals=None, is_register=False)
    emit("NAND", f"OUT{i}", (2700, 700 + 400 * i),
         f"read output of data bit {i} (inputs A/B, threshold 0.125): OUT = NAND(MA, "
         f"MB), the 2-to-1 MUX of the shipped example (#1059); the tap R{i} is renamed "
         f"here.",
         output_signals={"Y": f"R{i}"}, is_register=False)
    wire(f"CP0{i}", "Y2", f"MA{i}", "A")
    wire(f"CP1{i}", "Y2", f"MB{i}", "A")
    wire(f"MA{i}", "Y", f"OUT{i}", "A")
    wire(f"MB{i}", "Y", f"OUT{i}", "B")

_id_of = {g["GroupDto"]["GroupName"]: g["GroupDto"]["Identifier"] for g in GROUPS}
ids = list(_id_of.values())
if len(ids) != len(set(ids)):
    raise SystemExit("duplicate group identifiers")
expected_names = (["CPAX", "CPA", "NOTA", "CPNX", "CPN"]
                  + [n for w in (0, 1) for n in (f"EN{w}", f"CPEA{w}", f"CPEB{w}", f"IW{w}", f"CPI{w}")]
                  + [p + str(w) + str(i) for w in (0, 1) for i in (0, 1) for p in ("H", "LE", "REG", "CP")]
                  + [n for i in (0, 1) for n in (f"MA{i}", f"MB{i}", f"OUT{i}")])
if sorted(_id_of) != sorted(expected_names):
    raise SystemExit(f"group set mismatch: {sorted(_id_of)}")

out = {"FormatVersion": SRC["FormatVersion"], "Components": [], "Connections": [],
       "Groups": GROUPS, "Metadata": {"PdkVersions": {}, "Authorship": {
           "Created": "2026-08-21", "Modified": "2026-08-21T00:00:00.0000000Z"}},
       "ChipWidthMicrometers": SRC["ChipWidthMicrometers"],
       "ChipHeightMicrometers": SRC["ChipHeightMicrometers"]}
for start, start_pin, end, end_pin in WIRES:
    out["Connections"].append({
        "StartComponentIndex": 0, "StartPinName": start_pin,
        "EndComponentIndex": 0, "EndPinName": end_pin,
        "StartComponentId": _id_of[start], "EndComponentId": _id_of[end],
        "WidthMicrometers": 0.5, "BendRadiusMicrometers": 10})

TARGET.write_text(json.dumps(out, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {TARGET.name}: {len(GROUPS)} groups, {len(WIRES)} connections")

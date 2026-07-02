"""
list_pdk_resolution.py — Batch-verify that Lunima PDK ``nazcaFunction`` strings
resolve against the installed Python packages (issue #515).

Usage:
    python3 list_pdk_resolution.py --input <entries.json>

Input JSON (file):
    [ {"name": "Ring Resonator", "module": "demo", "function": "ring_resonator"},
      {"name": "Y-Branch 1550",  "module": "siepic_ebeam_pdk", "function": "ebeam_y_1550"} ]

Output (stdout): JSON
    { "success": true,
      "results": [ {"name": "...", "status": "ok"|"warning"|"error",
                    "kind": "callable"|"fixed-cell"|"static-cell"|"pcell"|"attribute"|"",
                    "message": "..."} ] }

On a run-level failure (e.g. unreadable input file):
    { "success": false, "error": "message" }

Per-entry import errors (nazca / siepic_ebeam_pdk not installed) are reported
as that entry's "error" status — one missing package must not abort the batch.
"""

import sys
import json
import os
import argparse
import contextlib

STATUS_OK = "ok"
STATUS_WARNING = "warning"
STATUS_ERROR = "error"


def _parse_args():
    parser = argparse.ArgumentParser(description="Verify PDK nazcaFunction strings")
    parser.add_argument("--input", required=True,
                        help="Path to a JSON file with a list of "
                             '{"name", "module", "function"} entries')
    return parser.parse_args()


def _result(name, status, kind, message):
    return {"name": name, "status": status, "kind": kind, "message": message}


def _resolve_demofab(name, module, function):
    """Resolve against nazca.demofab, walking dotted sub-paths like the
    preview script does ("demo.shallow" -> nazca.demofab.shallow)."""
    import nazca.demofab as mod
    walked = "nazca.demofab"
    sub_parts = module.split(".")[1:]  # drop leading "demo"
    for part in sub_parts:
        mod = getattr(mod, part, None)
        if mod is None:
            return _result(name, STATUS_ERROR, "",
                           f"module '{walked}' has no attribute '{part}'")
        walked += "." + part
    target = getattr(mod, function, None)
    if target is None:
        return _result(name, STATUS_ERROR, "",
                       f"module '{walked}' has no attribute '{function}'")
    if callable(target):
        return _result(name, STATUS_OK, "callable", f"{walked}.{function} is callable")
    return _result(name, STATUS_WARNING, "attribute",
                   f"{walked}.{function} exists but is not callable")


def _resolve_siepic(name, module, function):
    """Resolve a flat SiEPIC EBeam name: fixed-cell GDS file, static library
    cell, or registered PCell — mirrors the preview script's routing."""
    import importlib
    mod = importlib.import_module(module)
    pkg_dir = os.path.dirname(mod.__file__)

    gds_path = os.path.join(pkg_dir, "gds", "EBeam", f"{function}.gds")
    if os.path.exists(gds_path):
        return _result(name, STATUS_OK, "fixed-cell",
                       f"fixed-cell GDS: gds/EBeam/{function}.gds")

    try:
        import klayout.db as kdb
    except ImportError:
        return _result(name, STATUS_WARNING, "",
                       f"no fixed-cell GDS '{function}.gds'; klayout is not "
                       "installed so PCell lookup was skipped")

    for lid in kdb.Library.library_ids():
        lib = kdb.Library.library_by_id(lid)
        if lib is None or not lib.name().startswith("EBeam"):
            continue
        layout = lib.layout()
        if any(c.name == function for c in layout.each_cell()):
            return _result(name, STATUS_OK, "static-cell",
                           f"static cell in KLayout library '{lib.name()}'")
        if function in layout.pcell_names():
            return _result(name, STATUS_OK, "pcell",
                           f"PCell in KLayout library '{lib.name()}'")

    return _result(name, STATUS_ERROR, "",
                   f"'{function}' is neither a fixed-cell GDS nor a static cell "
                   "nor a PCell in any EBeam* library")


def _resolve_generic(name, module, function):
    """importlib + getattr for arbitrary module paths. When the full dotted
    module does not import, peel trailing segments off as attributes."""
    import importlib
    parts = module.split(".")
    mod = None
    attr_chain = []
    for cut in range(len(parts), 0, -1):
        try:
            mod = importlib.import_module(".".join(parts[:cut]))
            attr_chain = parts[cut:]
            break
        except ImportError:
            continue
    if mod is None:
        return _result(name, STATUS_ERROR, "", f"cannot import module '{module}'")

    walked = mod.__name__
    for part in attr_chain:
        mod = getattr(mod, part, None)
        if mod is None:
            return _result(name, STATUS_ERROR, "",
                           f"module '{walked}' has no attribute '{part}'")
        walked += "." + part

    target = getattr(mod, function, None)
    if target is None:
        return _result(name, STATUS_ERROR, "",
                       f"module '{walked}' has no attribute '{function}'")
    if callable(target):
        return _result(name, STATUS_OK, "callable", f"{walked}.{function} is callable")
    return _result(name, STATUS_WARNING, "attribute",
                   f"{walked}.{function} exists but is not callable")


def _resolve_entry(entry):
    name = entry.get("name", "")
    module = (entry.get("module") or "").strip()
    function = (entry.get("function") or "").strip()
    if not function:
        return _result(name, STATUS_ERROR, "", "empty nazcaFunction")
    try:
        if module.lower().startswith("siepic"):
            return _resolve_siepic(name, module, function)
        if module == "demo" or module.startswith("demo."):
            return _resolve_demofab(name, module, function)
        return _resolve_generic(name, module, function)
    except Exception as exc:  # per-entry isolation: one failure must not abort the batch
        return _result(name, STATUS_ERROR, "", str(exc))


def main():
    args = _parse_args()
    # Nazca prints chatter on stdout during import; keep stdout clean for
    # our JSON payload (same trick as render_component_preview.py).
    with contextlib.redirect_stdout(sys.stderr):
        try:
            with open(args.input, "r", encoding="utf-8") as f:
                entries = json.load(f)
            result = {"success": True,
                      "results": [_resolve_entry(e) for e in entries]}
        except Exception as exc:
            result = {"success": False, "error": str(exc)}
    print(json.dumps(result))
    sys.exit(0)


if __name__ == "__main__":
    main()

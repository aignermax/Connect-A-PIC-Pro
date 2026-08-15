#!/usr/bin/env python3
"""Run the vendored CORNERSTONE SiN 300nm KLayout pre-DRC deck on a GDS, headless.

Wraps `klayout -b -r <deck>.lydrc -rd input=<gds> -rd report=<lyrdb>`, parses the
resulting marker database and prints an agent-friendly per-rule violation summary.
KLayout itself exits 0 even when the deck flags violations, so the verdict is always
derived from the parsed report, never from klayout's exit code.

Usage:
    python3 scripts/run_cornerstone_drc.py chip.gds
    python3 scripts/run_cornerstone_drc.py chip.gds --report out.lyrdb --json
    python3 scripts/run_cornerstone_drc.py --parse-only out.lyrdb

KLayout discovery order: --klayout, $KLAYOUT, then klayout / klayout_app on PATH.

Exit codes: 0 = deck ran, no violations; 1 = violations found; 2 = tool/usage error.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

DECK_RELATIVE_PATH = os.path.join("drc", "cornerstone_sin300_drc.lydrc")
KLAYOUT_CANDIDATES = ("klayout", "klayout_app")
DEFAULT_TIMEOUT_SECONDS = 600

EXIT_CLEAN = 0
EXIT_VIOLATIONS = 1
EXIT_TOOL_ERROR = 2


def find_klayout(explicit):
    """Resolve the klayout executable: CLI arg, $KLAYOUT, then PATH candidates.

    Returns (path, None) on success, (None, attempted_override) when nothing usable
    was found, so the caller can print one precise error.
    """
    for candidate in (explicit, os.environ.get("KLAYOUT")):
        if candidate:
            if os.path.isfile(candidate) or shutil.which(candidate):
                return candidate, None
            return None, candidate
    for name in KLAYOUT_CANDIDATES:
        resolved = shutil.which(name)
        if resolved:
            return resolved, None
    return None, None


def parse_report(report_path):
    """Parse a .lyrdb marker database into {rule_name: violation_count}.

    Item categories are stored quoted ("'rule name'") inside <items>; the categories
    section lists every rule even when clean, so only <item> entries are counted.
    Raises ValueError on an unreadable/corrupt report so the caller can map it to a
    tool error — an unparseable report must never masquerade as a DRC verdict.
    """
    try:
        tree = ET.parse(report_path)
    except (ET.ParseError, OSError) as error:
        raise ValueError(f"cannot parse report {report_path}: {error}") from error
    root = tree.getroot()
    counts = {}
    items = root.find("items")
    if items is None:
        return counts
    for item in items.findall("item"):
        category = item.findtext("category", default="").strip()
        rule = category.strip("'")
        counts[rule] = counts.get(rule, 0) + 1
    return counts


def run_deck(klayout, deck, gds, report, timeout):
    """Run klayout batch DRC; returns (ok, detail)."""
    command = [
        klayout, "-b", "-r", deck,
        "-rd", f"input={gds}",
        "-rd", f"report={report}",
    ]
    try:
        completed = subprocess.run(
            command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, f"klayout timed out after {timeout}s"
    except OSError as error:
        return False, f"failed to launch klayout: {error}"
    if completed.returncode != 0:
        tail = (completed.stderr or completed.stdout or "").strip()[-2000:]
        return False, f"klayout exited with code {completed.returncode}:\n{tail}"
    if not os.path.isfile(report):
        tail = (completed.stderr or completed.stdout or "").strip()[-2000:]
        return False, f"klayout produced no report at {report}\n{tail}"
    return True, ""


def print_summary(gds, report, counts, as_json):
    """Emit the per-rule summary; returns the process exit code."""
    total = sum(counts.values())
    if as_json:
        payload = {
            "input": gds,
            "report": report,
            "totalViolations": total,
            "violationsByRule": dict(sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))),
            "passed": total == 0,
        }
        print(json.dumps(payload, indent=2))
        return EXIT_CLEAN if total == 0 else EXIT_VIOLATIONS

    print("CORNERSTONE SiN 300nm pre-DRC (vendored deck v2.1)")
    if gds:
        print(f"Input:   {gds}")
    print(f"Report:  {report}")
    print()
    for rule in sorted(counts, key=lambda r: (-counts[r], r)):
        print(f"  {counts[rule]} x {rule}")
    if total:
        print()
        print(f"FAILED: {total} DRC violation(s) across {len(counts)} rule(s).")
        return EXIT_VIOLATIONS
    print("PASSED: 0 DRC violations.")
    return EXIT_CLEAN


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Headless CORNERSTONE SiN 300nm pre-DRC via KLayout.")
    parser.add_argument("gds", nargs="?", help="GDS file to check")
    parser.add_argument("--deck", help="path to the .lydrc deck (default: vendored SiN 300nm deck)")
    parser.add_argument("--report", help="report database path (default: <gds stem>.lyrdb next to the GDS)")
    parser.add_argument("--klayout", help="klayout executable (default: $KLAYOUT or PATH)")
    parser.add_argument("--parse-only", metavar="REPORT",
                        help="skip klayout; summarize an existing .lyrdb report")
    parser.add_argument("--json", action="store_true", help="machine-readable JSON output")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT_SECONDS,
                        help=f"klayout timeout in seconds (default {DEFAULT_TIMEOUT_SECONDS})")
    args = parser.parse_args(argv)

    if args.parse_only:
        if not os.path.isfile(args.parse_only):
            print(f"error: report not found: {args.parse_only}", file=sys.stderr)
            return EXIT_TOOL_ERROR
        try:
            counts = parse_report(args.parse_only)
        except ValueError as error:
            print(f"error: {error}", file=sys.stderr)
            return EXIT_TOOL_ERROR
        return print_summary(None, args.parse_only, counts, args.json)

    if not args.gds:
        parser.error("a GDS path is required unless --parse-only is used")
    gds = os.path.abspath(args.gds)
    if not os.path.isfile(gds):
        print(f"error: GDS not found: {gds}", file=sys.stderr)
        return EXIT_TOOL_ERROR

    deck = os.path.abspath(args.deck) if args.deck else os.path.normpath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), DECK_RELATIVE_PATH))
    if not os.path.isfile(deck):
        print(f"error: DRC deck not found: {deck}", file=sys.stderr)
        return EXIT_TOOL_ERROR

    klayout, bad_override = find_klayout(args.klayout)
    if klayout is None:
        if bad_override:
            print(f"error: klayout not found at '{bad_override}'", file=sys.stderr)
        else:
            print(
                "error: no KLayout executable found. Install KLayout (https://www.klayout.de), "
                "put klayout on PATH, or pass --klayout / set $KLAYOUT.",
                file=sys.stderr)
        return EXIT_TOOL_ERROR

    report = os.path.abspath(args.report) if args.report else os.path.splitext(gds)[0] + ".lyrdb"
    ok, detail = run_deck(klayout, deck, gds, report, args.timeout)
    if not ok:
        print(f"error: {detail}", file=sys.stderr)
        return EXIT_TOOL_ERROR

    try:
        counts = parse_report(report)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return EXIT_TOOL_ERROR
    return print_summary(gds, report, counts, args.json)


if __name__ == "__main__":
    sys.exit(main())

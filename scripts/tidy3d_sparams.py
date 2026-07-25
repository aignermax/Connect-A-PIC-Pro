"""
tidy3d_sparams.py - Tidy3D cloud FDTD S-matrix bridge for Lunima.

Reads a JSON spec from stdin (same geometry contract as fdtd_sparams.py plus a
"mode" selector), talks to the Tidy3D cloud, and writes a JSON result to stdout.
Live status lines go to stderr with a LUNIMA_PROGRESS: prefix — the app forwards
only those to its status line.

The API key is taken from the SIMCLOUD_APIKEY environment variable (set by the
app from its settings). The user's existing ~/.tidy3d/config is only a fallback
for direct CLI usage of this script — the app refuses to launch it keyless.

Requires: pip install "tidy3d>=2.10" gdstk

Input JSON (stdin) - solve/estimate:
    {
      "mode":        "check" | "estimate" | "solve",   # default "solve"
      "gds_path":    "/path/component.gds",            # or "polygons"
      "polygons":    [ {"layer": 1, "points": [[x,y], ...]}, ... ],
      "ports": [ {"name":"o1","x":0.0,"y":0.0,"orientation":180,"width":0.5}, ... ],
      "layer":       [1, 0],
      "wavelength_start": 1.5, "wavelength_stop": 1.6, "wavelength_points": 11,
      "ymargin": 2.0, "xmargin": 2.0
      # optional material overrides:
      # "core_thickness": 0.22, "core_index": 3.48, "clad_index": 1.44
    }

Output JSON (stdout):
    check:    { "success": true, "tidy3d_version": "2.10.1", "api_key_configured": true }
    estimate: { "success": true, "estimated_credits": 0.35, "simulation_count": 2 }
    solve:    same contract as fdtd_sparams.py:
              { "success": true, "is_3d": true, "ports": [...], "wavelengths": [...],
                "s": {"o2@0,o1@0": [[re,im], ...], ...},
                "energy_sum_per_input": {"o1@0": 0.99, ...} }
    failure:  { "success": false, "error": "...", "missing_backend": "tidy3d"|"gdstk"|null }
              plus "missing_api_key": true on the keyless path and "trace" on a crash.
"""

import json
import os
import sys

C_UM_PER_S = 299792458e6  # speed of light in um/s

DEFAULT_CORE_THICKNESS = 0.22   # um, standard 220 nm SOI
DEFAULT_CORE_INDEX = 3.48       # silicon
DEFAULT_CLAD_INDEX = 1.44       # silica
PORT_MODE_SIZE_FACTOR = 3.0     # port plane extent relative to waveguide width
Z_MARGIN = 1.0                  # um of cladding above/below the core
RUN_TIME_PS = 20.0              # generous FDTD run time budget (auto shutoff ends earlier)

# smatrix.ModalComponentModeler and run(verbose=...) only exist in tidy3d >= 2.10.
MIN_TIDY3D_VERSION = (2, 10)

PROGRESS_PREFIX = "LUNIMA_PROGRESS:"
PROGRESS_POLL_SECONDS = 15.0    # cloud batch status poll interval during solve

# tidy3d task states worth surfacing (see tidy3d.web.api.states in 2.10).
_PROGRESS_STATES = ("queued", "preprocess", "running", "postprocess", "success")
_ERROR_STATES = ("validate_error", "preprocess_error", "run_error", "postprocess_error",
                 "error", "errored", "blocked", "aborted", "deleted",
                 "diverge", "diverged")
_DONE_STATES = ("success", "completed", "processed", "postprocess_success")


def _progress(msg):
    print(f"{PROGRESS_PREFIX} {msg}", file=sys.stderr, flush=True)


def _emit(obj):
    print(json.dumps(obj), flush=True)


def _fail(message, missing_backend=None, missing_api_key=False):
    payload = {"success": False, "error": message, "missing_backend": missing_backend}
    if missing_api_key:
        payload["missing_api_key"] = True
    _emit(payload)
    sys.exit(0 if missing_backend else 1)


def _import_tidy3d():
    try:
        import tidy3d  # noqa: F401
        return tidy3d
    except ImportError:
        _fail(
            "The tidy3d package is not installed in the selected Python "
            "environment. Install it with: pip install tidy3d gdstk",
            missing_backend="tidy3d",
        )


def _import_gdstk():
    try:
        import gdstk  # noqa: F401
        return gdstk
    except ImportError:
        _fail(
            "The gdstk package is not installed in the selected Python "
            "environment (needed to read GDS geometry). Install it with: "
            "pip install tidy3d gdstk",
            missing_backend="gdstk",
        )


def _tidy3d_version_too_old(version_str):
    """True when the installed tidy3d predates the ModalComponentModeler API
    (MIN_TIDY3D_VERSION). Tolerant parse: an unparseable version counts as too old."""
    try:
        parts = tuple(int(x) for x in str(version_str).split(".")[:2])
    except (ValueError, AttributeError):
        return True
    return parts < MIN_TIDY3D_VERSION


def _api_key_configured():
    if os.environ.get("SIMCLOUD_APIKEY"):
        return True
    config = os.path.join(os.path.expanduser("~"), ".tidy3d", "config")
    return os.path.isfile(config)


def check():
    td = _import_tidy3d()
    version = getattr(td, "__version__", "unknown")
    if _tidy3d_version_too_old(version):
        _fail(
            f"tidy3d >= {MIN_TIDY3D_VERSION[0]}.{MIN_TIDY3D_VERSION[1]} required "
            f"(found {version}). Upgrade with: pip install -U tidy3d",
            missing_backend="tidy3d",
        )
    _import_gdstk()
    if not _api_key_configured():
        _fail(
            "No Tidy3D API key configured. Get one at https://tidy3d.simulation.cloud "
            "and enter it in Settings → Tidy3D Cloud.",
            missing_api_key=True,
        )
    _emit({
        "success": True,
        "tidy3d_version": version,
        "api_key_configured": True,
    })


def _sim_xy_bounds(ports, xmargin, ymargin):
    """Simulation XY extent: the port-center span padded by the margins. The port
    extension stubs reach exactly to these bounds (margin replaced by waveguide),
    so the sim size is unchanged by the extensions."""
    xs = [float(p["x"]) for p in ports]
    ys = [float(p["y"]) for p in ports]
    return (min(xs) - xmargin, max(xs) + xmargin,
            min(ys) - ymargin, max(ys) + ymargin)


def _port_extension_rects(ports, bounds):
    """Axis-aligned waveguide stubs extending each port's core along its outward
    normal up to the simulation boundary.

    ModalComponentModeler shifts each mode SOURCE ~2 grid cells upstream of the
    port (monitor) plane (ModalComponentModeler.shift_port, tidy3d 2.10) — with
    geometry flush at the port, the source would sit in bare cladding and excite
    cladding modes, producing wrong S-matrices. A stub of the port's width keeps
    the source inside the guide (same idea as gplugins' meep write_sparameters
    port extensions). All geometry is single-layer core, so the stub reuses the
    port's slab bounds and core medium. Pure function: checkable without tidy3d.
    """
    x0, x1, y0, y1 = bounds
    rects = []
    for p in ports:
        px, py = float(p["x"]), float(p["y"])
        half = float(p["width"]) / 2.0
        orientation = float(p["orientation"]) % 360.0
        # Orientation = direction the waveguide leaves the device (outward normal).
        if orientation == 0.0:        # east edge: stub to the +x boundary
            rects.append([(px, py - half), (x1, py - half), (x1, py + half), (px, py + half)])
        elif orientation == 180.0:    # west edge: stub to the -x boundary
            rects.append([(x0, py - half), (px, py - half), (px, py + half), (x0, py + half)])
        elif orientation == 90.0:     # north edge: stub to the +y boundary
            rects.append([(px - half, py), (px + half, py), (px + half, y1), (px - half, y1)])
        elif orientation == 270.0:    # south edge: stub to the -y boundary
            rects.append([(px - half, y0), (px + half, y0), (px + half, py), (px - half, py)])
        # Non-manhattan orientations are rejected in _build_ports.
    return rects


def _load_geometry(td, spec):
    """Returns (structures, thickness): the component core geometry plus, per
    port, a waveguide stub reaching the simulation boundary (see
    _port_extension_rects)."""
    layer = tuple(spec.get("layer", [1, 0]))
    thickness = float(spec.get("core_thickness", DEFAULT_CORE_THICKNESS))
    core = td.Medium(permittivity=float(spec.get("core_index", DEFAULT_CORE_INDEX)) ** 2)
    slab_bounds = (0.0, thickness)

    polygons = spec.get("polygons") or []
    geometries = []
    if polygons:
        for poly in polygons:
            pts = [(float(x), float(y)) for x, y in poly["points"]]
            geometries.append(td.PolySlab(vertices=pts, slab_bounds=slab_bounds, axis=2))
    else:
        gdstk = _import_gdstk()
        lib = gdstk.read_gds(spec["gds_path"])
        cell = lib.top_level()[0]
        geometries = td.PolySlab.from_gds(
            cell, gds_layer=int(layer[0]), gds_dtype=int(layer[1]),
            slab_bounds=slab_bounds, axis=2,
        )

    ports = spec.get("ports") or []
    if ports:
        bounds = _sim_xy_bounds(
            ports,
            float(spec.get("xmargin", 2.0)),
            float(spec.get("ymargin", 2.0)),
        )
        geometries.extend(
            td.PolySlab(vertices=rect, slab_bounds=slab_bounds, axis=2)
            for rect in _port_extension_rects(ports, bounds)
        )

    return [td.Structure(geometry=g, medium=core) for g in geometries], thickness


def _build_ports(td, smatrix, spec, thickness):
    """Maps Lunima's port list to tidy3d ComponentModeler ports."""
    ports = []
    for p in spec["ports"]:
        orientation = float(p["orientation"]) % 360.0
        # Ports face into the simulation: 0° = pointing +x out of the device.
        if orientation in (0.0, 180.0):
            axis_direction = "-" if orientation == 0.0 else "+"
        elif orientation in (90.0, 270.0):
            axis_direction = "-" if orientation == 90.0 else "+"
        else:
            _fail(f"Port '{p['name']}' has non-manhattan orientation {orientation}.")
        horizontal = orientation in (0.0, 180.0)
        width = float(p["width"])
        size_lateral = PORT_MODE_SIZE_FACTOR * width
        size_z = thickness + 2 * Z_MARGIN
        ports.append(smatrix.Port(
            center=(float(p["x"]), float(p["y"]), thickness / 2.0),
            size=(0, size_lateral, size_z) if horizontal else (size_lateral, 0, size_z),
            direction=axis_direction,
            mode_spec=td.ModeSpec(num_modes=1),
            name=str(p["name"]),
        ))
    return ports


def _build_modeler(td, spec):
    from tidy3d.plugins import smatrix
    import numpy as np

    structures, thickness = _load_geometry(td, spec)
    ports = _build_ports(td, smatrix, spec, thickness)

    xmargin = float(spec.get("xmargin", 2.0))
    ymargin = float(spec.get("ymargin", 2.0))
    x0, x1, y0, y1 = _sim_xy_bounds(spec["ports"], xmargin, ymargin)
    center = ((x0 + x1) / 2, (y0 + y1) / 2, thickness / 2)

    lambdas = np.linspace(
        float(spec.get("wavelength_start", 1.5)),
        float(spec.get("wavelength_stop", 1.6)),
        int(spec.get("wavelength_points", 11)),
    )
    freqs = C_UM_PER_S / lambdas

    clad = td.Medium(permittivity=float(spec.get("clad_index", DEFAULT_CLAD_INDEX)) ** 2)
    sim = td.Simulation(
        center=center,
        size=(x1 - x0, y1 - y0, thickness + 2 * Z_MARGIN),
        structures=structures,
        medium=clad,
        grid_spec=td.GridSpec.auto(wavelength=float(lambdas.mean())),
        boundary_spec=td.BoundarySpec.all_sides(boundary=td.PML()),
        run_time=RUN_TIME_PS * 1e-12,
    )

    modeler = smatrix.ModalComponentModeler(
        simulation=sim, ports=ports, freqs=list(freqs),
    )
    return modeler, lambdas


def _poll_batch_progress(web, jobs, last_statuses, last_done):
    """Emits per-task status transitions and a done-count for an in-flight batch;
    returns the new (last_statuses, last_done). Raises on API drift — the caller
    degrades to silence."""
    statuses = {}
    for name, job in jobs.items():
        # Read the uploaded task id straight from the cache: Job.task_id would
        # UPLOAD the job if not uploaded yet — a duplicate, double-billed task.
        task_id = getattr(job, "_cached_properties", {}).get("task_id")
        if task_id:
            statuses[name] = str(web.get_info(task_id).status)
    for name, status in statuses.items():
        if status != last_statuses.get(name) and status in _PROGRESS_STATES + _ERROR_STATES:
            _progress(f"task {name}: {status}")
    done = sum(1 for s in statuses.values() if s in _DONE_STATES)
    if done > 0 and done != last_done:
        _progress(f"{done}/{len(jobs)} simulations done")
    return statuses, done


def _run_modeler(modeler):
    """Runs the modeler's cloud batch, emitting LUNIMA_PROGRESS lines meanwhile.

    tidy3d 2.10's modeler.run() is a blocking Batch run whose own progress bars
    vanish with verbose=False, so we reproduce its exact two steps (see
    tidy3d.plugins.smatrix.run._run_local) around a pollable Batch handle. Any
    API drift falls back to the plain blocking run: progress degrades to
    silence, never a failed solve.
    """
    import threading

    try:
        from tidy3d import web
        from tidy3d.plugins.smatrix.run import compose_modeler_data_from_batch_data

        batch = web.Batch(simulations=modeler.sim_dict, verbose=False)
        # Force the jobs cached_property on THIS thread so the poller shares the
        # same Job objects the run thread uploads (never builds a second set).
        jobs = batch.jobs
    except Exception:
        return modeler.run(verbose=False)

    outcome = {}

    def _worker():
        try:
            batch_data = batch.run(path_dir=".")
            outcome["smatrix"] = compose_modeler_data_from_batch_data(
                modeler=modeler, batch_data=batch_data).smatrix()
        except Exception as exc:  # re-raised on the main thread below
            outcome["error"] = exc

    worker = threading.Thread(target=_worker, daemon=True)
    worker.start()
    last_statuses, last_done = {}, -1
    while worker.is_alive():
        worker.join(PROGRESS_POLL_SECONDS)
        if not worker.is_alive():
            break
        try:
            last_statuses, last_done = _poll_batch_progress(
                web, jobs, last_statuses, last_done)
        except Exception:
            pass  # transient HTTP/API differences → silence, the run continues
    worker.join()
    if "error" in outcome:
        raise outcome["error"]
    return outcome["smatrix"]


def estimate(spec):
    td = _import_tidy3d()
    from tidy3d import web
    modeler, _ = _build_modeler(td, spec)
    sims = list(modeler.sim_dict.values())
    _progress("Uploading simulation for cost estimate…")
    task_id = web.upload(sims[0], task_name="lunima-estimate", verbose=False)
    try:
        total = float(web.estimate_cost(task_id, verbose=False)) * len(sims)
    finally:
        try:
            web.delete(task_id)
        except Exception:
            pass
    _emit({"success": True, "estimated_credits": total, "simulation_count": len(sims)})


def solve(spec):
    td = _import_tidy3d()
    import numpy as np

    modeler, lambdas = _build_modeler(td, spec)
    _progress(f"Submitting {len(modeler.ports)} simulation(s) to the Tidy3D cloud…")
    s_matrix = _run_modeler(modeler)

    port_names = [p.name for p in modeler.ports]
    s_out, energy_sum = {}, {}
    for p_in in port_names:
        for p_out in port_names:
            arr = np.asarray(
                s_matrix.sel(port_in=p_in, mode_index_in=0, port_out=p_out, mode_index_out=0)
            ).ravel()
            key = f"{p_out}@0,{p_in}@0"
            s_out[key] = [[float(z.real), float(z.imag)] for z in arr]
            in_key = f"{p_in}@0"
            energy_sum[in_key] = energy_sum.get(in_key, 0.0) + float(
                abs(arr[len(arr) // 2]) ** 2
            )

    _emit({
        "success": True,
        "is_3d": True,  # Tidy3D always runs full 3D
        "ports": port_names,
        "wavelengths": [float(x) for x in lambdas],
        "s": s_out,
        "energy_sum_per_input": energy_sum,
    })


def main():
    spec = json.loads(sys.stdin.read())
    mode = spec.get("mode", "solve")
    if mode == "check":
        check()
    elif mode == "estimate":
        estimate(spec)
    elif mode == "solve":
        solve(spec)
    else:
        _fail(f"Unknown mode '{mode}'.")


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception as e:
        import traceback
        msg = str(e)
        # Map cloud auth failures to an actionable message instead of the raw HTTP body.
        if "401" in msg or "Unauthorized" in msg or "API key not found" in msg:
            msg = ("Tidy3D rejected the API key (HTTP 401). Check the key in "
                   "Settings → Tidy3D Cloud.")
        _emit({"success": False, "error": msg, "trace": traceback.format_exc()[-1500:]})
        sys.exit(1)

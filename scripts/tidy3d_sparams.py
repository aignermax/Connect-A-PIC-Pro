"""
tidy3d_sparams.py - Tidy3D cloud FDTD S-matrix bridge for Lunima.

Reads a JSON spec from stdin (same geometry contract as fdtd_sparams.py plus a
"mode" selector), talks to the Tidy3D cloud, and writes a JSON result to stdout.
Progress lines go to stderr so the app can stream live status.

The API key is taken from the SIMCLOUD_APIKEY environment variable (set by the
app from its settings) or the user's existing ~/.tidy3d/config.

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
    check:    { "success": true, "tidy3d_version": "2.7.0", "api_key_configured": true }
    estimate: { "success": true, "estimated_credits": 0.35, "simulation_count": 2 }
    solve:    same contract as fdtd_sparams.py:
              { "success": true, "is_3d": true, "ports": [...], "wavelengths": [...],
                "s": {"o2@0,o1@0": [[re,im], ...], ...},
                "energy_sum_per_input": {"o1@0": 0.99, ...} }
    failure:  { "success": false, "error": "...", "missing_backend": "tidy3d"|null }
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


def _progress(msg):
    print(msg, file=sys.stderr, flush=True)


def _emit(obj):
    print(json.dumps(obj), flush=True)


def _fail(message, missing_backend=None):
    _emit({"success": False, "error": message, "missing_backend": missing_backend})
    sys.exit(0 if missing_backend else 1)


def _import_tidy3d():
    try:
        import tidy3d  # noqa: F401
        return tidy3d
    except ImportError:
        _fail(
            "The tidy3d package is not installed in the selected Python "
            "environment. Install it with: pip install tidy3d",
            missing_backend="tidy3d",
        )


def _api_key_configured():
    if os.environ.get("SIMCLOUD_APIKEY"):
        return True
    config = os.path.join(os.path.expanduser("~"), ".tidy3d", "config")
    return os.path.isfile(config)


def check():
    td = _import_tidy3d()
    if not _api_key_configured():
        _fail(
            "No Tidy3D API key configured. Get one at https://tidy3d.simulation.cloud "
            "and enter it in Settings → Tidy3D Cloud."
        )
    _emit({
        "success": True,
        "tidy3d_version": getattr(td, "__version__", "unknown"),
        "api_key_configured": True,
    })


def _load_geometry(td, spec):
    """Returns a list of td.Structure for the component core geometry."""
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
        import gdstk
        lib = gdstk.read_gds(spec["gds_path"])
        cell = lib.top_level()[0]
        geometries = td.PolySlab.from_gds(
            cell, gds_layer=int(layer[0]), gds_dtype=int(layer[1]),
            slab_bounds=slab_bounds, axis=2,
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

    xs = [p.center[0] for p in ports]
    ys = [p.center[1] for p in ports]
    xmargin = float(spec.get("xmargin", 2.0))
    ymargin = float(spec.get("ymargin", 2.0))
    size_x = (max(xs) - min(xs)) + 2 * xmargin
    size_y = (max(ys) - min(ys)) + 2 * ymargin
    center = ((max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, thickness / 2)

    lambdas = np.linspace(
        float(spec.get("wavelength_start", 1.5)),
        float(spec.get("wavelength_stop", 1.6)),
        int(spec.get("wavelength_points", 11)),
    )
    freqs = C_UM_PER_S / lambdas

    clad = td.Medium(permittivity=float(spec.get("clad_index", DEFAULT_CLAD_INDEX)) ** 2)
    sim = td.Simulation(
        center=center,
        size=(size_x, size_y, thickness + 2 * Z_MARGIN),
        structures=structures,
        medium=clad,
        grid_spec=td.GridSpec.auto(wavelength=float(lambdas.mean())),
        boundary_spec=td.BoundarySpec.all_sides(boundary=td.PML()),
        run_time=RUN_TIME_PS * 1e-12,
    )

    modeler = smatrix.ComponentModeler(
        simulation=sim, ports=ports, freqs=list(freqs), verbose=True,
    )
    return modeler, lambdas


def estimate(spec):
    td = _import_tidy3d()
    modeler, _ = _build_modeler(td, spec)
    _progress("Uploading simulation for cost estimate…")
    sim_count = len(modeler.ports)
    try:
        credits_one = float(modeler.batch.estimate_cost())
        total = credits_one
    except Exception:
        # Older tidy3d: estimate a single port simulation and scale.
        from tidy3d import web
        sim = list(modeler.sim_dict.values())[0]
        task_id = web.upload(sim, task_name="lunima-estimate", verbose=False)
        try:
            total = float(web.estimate_cost(task_id)) * sim_count
        finally:
            try:
                web.delete(task_id)
            except Exception:
                pass
    _emit({"success": True, "estimated_credits": total, "simulation_count": sim_count})


def solve(spec):
    td = _import_tidy3d()
    import numpy as np

    modeler, lambdas = _build_modeler(td, spec)
    _progress(f"Submitting {len(modeler.ports)} simulation(s) to the Tidy3D cloud…")
    s_matrix = modeler.run()

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
        _emit({"success": False, "error": str(e), "trace": traceback.format_exc()[-1500:]})
        sys.exit(1)

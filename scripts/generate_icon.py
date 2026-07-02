#!/usr/bin/env python3
"""
Generate the Lunima application icon (LunimaIcon.ico and LunimaIcon.icns).

Produces a professional photonic/waveguide-themed icon with multiple sizes
suitable for Windows applications (ICO), MSI installers, and macOS (ICNS).

Usage:
    python3 scripts/generate_icon.py [--output Installer/LunimaIcon.ico] [--icns Installer/LunimaIcon.icns]
"""

import argparse
import math
import os
import platform
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


# ---------------------------------------------------------------------------
# Color palette
# ---------------------------------------------------------------------------
BG_COLOR       = (12, 18, 42)        # Deep navy blue – photonic darkness
WAVEGUIDE_DARK = (20, 120, 200)      # Deep cyan-blue waveguide body
WAVEGUIDE_MID  = (40, 180, 255)      # Mid cyan waveguide highlight
WAVEGUIDE_GLOW = (120, 220, 255)     # Bright glow on waveguide core
LIGHT_DOT_CORE = (255, 255, 220)     # Near-white for light spots
LIGHT_DOT_HALO = (180, 240, 255, 80) # Translucent halo around light spots
ACCENT_GOLD    = (255, 200, 60)      # Gold accent for energy


def _lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def _draw_rounded_rect(
    draw: ImageDraw.ImageDraw,
    x0: float, y0: float, x1: float, y1: float,
    radius: float,
    fill: tuple,
) -> None:
    """Draw a rounded rectangle."""
    draw.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=fill)


def _bezier_points(p0, p1, p2, p3, steps: int = 60):
    """Return (steps+1) points along a cubic Bézier curve."""
    pts = []
    for i in range(steps + 1):
        t = i / steps
        mt = 1 - t
        x = mt**3 * p0[0] + 3*mt**2*t * p1[0] + 3*mt*t**2 * p2[0] + t**3 * p3[0]
        y = mt**3 * p0[1] + 3*mt**2*t * p1[1] + 3*mt*t**2 * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def _draw_thick_polyline(draw: ImageDraw.ImageDraw, pts, width: int, color: tuple) -> None:
    """Draw a thick poly-line by painting circles at each sample point."""
    r = width / 2
    for x, y in pts:
        draw.ellipse([x - r, y - r, x + r, y + r], fill=color)


def _glow_layer(size: int, pts, width: int, color: tuple) -> Image.Image:
    """Render a single glow pass and return an RGBA image."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    _draw_thick_polyline(draw, pts, width, color)
    return layer.filter(ImageFilter.GaussianBlur(radius=width * 0.8))


def render_icon(size: int) -> Image.Image:
    """Render the Lunima icon at *size* × *size* pixels."""
    s = size
    img = Image.new("RGBA", (s, s), BG_COLOR + (255,))
    draw = ImageDraw.Draw(img)

    # -----------------------------------------------------------------------
    # Background: subtle radial gradient via concentric ellipses
    # -----------------------------------------------------------------------
    cx, cy = s / 2, s / 2
    max_r = s * 0.52
    steps = 20
    for i in range(steps, 0, -1):
        t = i / steps
        r = max_r * t
        alpha = int(40 * (1 - t))
        color = (20, 60, 120, alpha)
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color)

    # -----------------------------------------------------------------------
    # Waveguide paths (two S-curves representing photonic waveguides)
    # -----------------------------------------------------------------------
    margin = s * 0.10
    mid    = s * 0.50
    q1     = s * 0.25
    q3     = s * 0.75
    ctrl_off = s * 0.28

    # Top waveguide: enters left-center, curves through top, exits right
    wg1_p0 = (margin,       mid - s * 0.12)
    wg1_p1 = (margin + ctrl_off, mid - s * 0.12)
    wg1_p2 = (s - margin - ctrl_off, q1 - s * 0.05)
    wg1_p3 = (s - margin,   q1 - s * 0.05)

    # Bottom waveguide: symmetric
    wg2_p0 = (margin,       mid + s * 0.12)
    wg2_p1 = (margin + ctrl_off, mid + s * 0.12)
    wg2_p2 = (s - margin - ctrl_off, q3 + s * 0.05)
    wg2_p3 = (s - margin,   q3 + s * 0.05)

    pts1 = _bezier_points(wg1_p0, wg1_p1, wg1_p2, wg1_p3)
    pts2 = _bezier_points(wg2_p0, wg2_p1, wg2_p2, wg2_p3)

    base_w = max(2, int(s * 0.065))
    glow_w = max(4, int(s * 0.130))
    core_w = max(1, int(s * 0.030))

    for pts in (pts1, pts2):
        # Outer glow (blurred)
        glow = _glow_layer(s, pts, glow_w, WAVEGUIDE_DARK + (160,))
        img = Image.alpha_composite(img, glow)

        # Mid body
        draw_mid = ImageDraw.Draw(img)
        _draw_thick_polyline(draw_mid, pts, base_w, WAVEGUIDE_MID)

        # Bright core
        draw_core = ImageDraw.Draw(img)
        _draw_thick_polyline(draw_core, pts, core_w, WAVEGUIDE_GLOW)

    # -----------------------------------------------------------------------
    # Light spots: glowing dots at entry and exit points
    # -----------------------------------------------------------------------
    spot_pts = [wg1_p0, wg1_p3, wg2_p0, wg2_p3]
    spot_r   = max(3, int(s * 0.060))
    halo_r   = max(5, int(s * 0.120))

    for px, py in spot_pts:
        # Halo (blurred soft glow)
        halo_layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        halo_draw  = ImageDraw.Draw(halo_layer)
        halo_draw.ellipse(
            [px - halo_r, py - halo_r, px + halo_r, py + halo_r],
            fill=LIGHT_DOT_HALO,
        )
        halo_blurred = halo_layer.filter(ImageFilter.GaussianBlur(radius=halo_r * 0.7))
        img = Image.alpha_composite(img, halo_blurred)

        # Bright core dot
        dot_draw = ImageDraw.Draw(img)
        dot_draw.ellipse(
            [px - spot_r, py - spot_r, px + spot_r, py + spot_r],
            fill=LIGHT_DOT_CORE,
        )

    # -----------------------------------------------------------------------
    # Gold coupling region: small arcs in the center suggesting evanescent
    # coupling between the two waveguides
    # -----------------------------------------------------------------------
    if s >= 32:
        arc_cx, arc_cy = cx, mid
        ar = max(3, int(s * 0.055))
        arc_color = ACCENT_GOLD
        arc_draw  = ImageDraw.Draw(img)
        for offset in (-ar * 1.4, 0, ar * 1.4):
            x0 = arc_cx + offset - ar
            y0 = arc_cy - ar
            x1 = arc_cx + offset + ar
            y1 = arc_cy + ar
            arc_draw.arc([x0, y0, x1, y1], start=220, end=320, fill=arc_color,
                         width=max(1, int(s * 0.018)))

    # -----------------------------------------------------------------------
    # Rounded-rect border (subtle frame)
    # -----------------------------------------------------------------------
    border_draw = ImageDraw.Draw(img)
    bw = max(1, int(s * 0.020))
    border_draw.rounded_rectangle(
        [bw, bw, s - bw - 1, s - bw - 1],
        radius=max(4, int(s * 0.12)),
        outline=WAVEGUIDE_MID + (120,),
        width=bw,
    )

    return img


def build_ico(output_path: Path) -> None:
    """Build a multi-resolution ICO file and write it to *output_path*.

    Pillow's ICO encoder expects a single source image and downscales it to the
    requested sizes list.  We render at the largest size for maximum quality.
    """
    sizes = [16, 24, 32, 48, 64, 128, 256]
    source = render_icon(256).convert("RGBA")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    source.save(
        output_path,
        format="ICO",
        sizes=[(sz, sz) for sz in sizes],
    )
    print(f"[generate_icon] Written: {output_path}  ({len(sizes)} sizes: {sizes})")


def build_icns(output_path: Path) -> None:
    """Build a macOS ICNS file and write it to *output_path*.

    On macOS, renders standard iconset sizes, creates a .iconset directory,
    and uses 'iconutil -c icns' to generate the ICNS file.

    On other platforms, uses Pillow's ICNS encoder as a fallback.
    """
    # Standard macOS iconset sizes (in points, but we render at 1x pixel density)
    iconset_sizes = [16, 32, 64, 128, 256, 512, 1024]
    source = render_icon(1024)

    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Try iconutil on macOS
    if platform.system() == "Darwin":
        try:
            with tempfile.TemporaryDirectory() as tmpdir:
                iconset_dir = Path(tmpdir) / "LunimaIcon.iconset"
                iconset_dir.mkdir(parents=True, exist_ok=True)

                # Render each size and save as .png in the iconset directory
                for size in iconset_sizes:
                    icon = render_icon(size).convert("RGBA")
                    icon_path = iconset_dir / f"icon_{size}x{size}.png"
                    icon.save(icon_path, format="PNG")

                # Convert iconset to ICNS using iconutil
                subprocess.run(
                    ["iconutil", "-c", "icns", "-o", str(output_path), str(iconset_dir)],
                    check=True,
                    capture_output=True,
                )
                print(f"[generate_icon] Written: {output_path}  ({len(iconset_sizes)} sizes: {iconset_sizes})")
                return
        except (FileNotFoundError, subprocess.CalledProcessError) as e:
            print(f"[generate_icon] Warning: iconutil failed, falling back to Pillow: {e}")

    # Fallback: use Pillow's ICNS encoder
    try:
        source.save(output_path, format="ICNS")
        print(f"[generate_icon] Written: {output_path}  (Pillow ICNS fallback, {len(iconset_sizes)} sizes: {iconset_sizes})")
    except Exception as e:
        print(f"[generate_icon] Error: Unable to generate ICNS file: {e}")
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate the Lunima application icons (ICO and ICNS)")
    parser.add_argument(
        "--output",
        default="Installer/LunimaIcon.ico",
        help="Output path for the .ico file (default: Installer/LunimaIcon.ico)",
    )
    parser.add_argument(
        "--icns",
        default=None,
        help="Output path for the .icns file (default: Installer/LunimaIcon.icns, omit to skip)",
    )
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent

    # Generate ICO file
    output_path = (repo_root / args.output).resolve()
    build_ico(output_path)

    # Generate ICNS file if requested
    icns_path = args.icns
    if icns_path is None:
        icns_path = "Installer/LunimaIcon.icns"
    icns_output_path = (repo_root / icns_path).resolve()
    build_icns(icns_output_path)


if __name__ == "__main__":
    main()

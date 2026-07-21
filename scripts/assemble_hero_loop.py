"""Assembles docs/media/v0.12/hero-loop.gif from the frames rendered by
ShowcaseHeroLoopScreenshotTests (UnitTests/UI/Showcase).

The frames show the hero chip building itself: components placed, the router
wiring connection after connection, the DC metal traces, the hand-styled bends,
and finally the CW power-flow overlay. Timing: a longer hold on the first
(placement) frame, quick steps while routing, a beat on the fully routed chip,
and the longest hold on the simulation payoff before the loop restarts.

Usage:
    python assemble_hero_loop.py <frames_dir> <out.gif> [width]

Requires Pillow (any Lunima-managed or gdsfactory venv has it via matplotlib).
"""
import pathlib
import sys

from PIL import Image
from PIL import GifImagePlugin  # noqa: F401 — explicit so the .gif saver is registered

FIRST_HOLD_MS = 500
STEP_MS = 120
ROUTED_HOLD_MS = 400
PAYOFF_HOLD_MS = 1400
DEFAULT_WIDTH = 960


def main() -> None:
    frames_dir = pathlib.Path(sys.argv[1])
    out_path = pathlib.Path(sys.argv[2])
    width = int(sys.argv[3]) if len(sys.argv) > 3 else DEFAULT_WIDTH

    paths = sorted(frames_dir.glob("frame_*.png"))
    if len(paths) < 4:
        raise SystemExit(f"expected at least 4 frames in {frames_dir}, found {len(paths)}")

    frames = []
    for path in paths:
        image = Image.open(path).convert("RGB")
        height = round(image.height * width / image.width)
        frames.append(image.resize((width, height), Image.LANCZOS))

    durations = (
        [FIRST_HOLD_MS]
        + [STEP_MS] * (len(frames) - 3)
        + [ROUTED_HOLD_MS, PAYOFF_HOLD_MS]
    )
    frames[0].save(
        out_path,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        optimize=True,
    )
    size_kib = out_path.stat().st_size / 1024
    total_s = sum(durations) / 1000
    print(f"{out_path}: {len(frames)} frames, {total_s:.1f}s loop, {size_kib:.0f} KiB")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Remove the supplied logo background with rembg and create a clean master PNG."""

from __future__ import annotations

import argparse
from io import BytesIO
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter
from rembg import new_session, remove


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "assets" / "ESPxBenchmark_logo.jpg"
DEFAULT_OUTPUT = ROOT / "assets" / "final" / "logo-master.png"


def process(source: Path, output: Path, scale: int) -> None:
    if not source.is_file():
        raise FileNotFoundError(f"Logo source does not exist: {source}")
    if scale < 1:
        raise ValueError("--scale must be at least 1")

    session = new_session("u2net")
    segmented = remove(source.read_bytes(), session=session, post_process_mask=True)
    with Image.open(source) as source_image, Image.open(BytesIO(segmented)) as result:
        rgb = np.asarray(source_image.convert("RGB"), dtype=np.float32)
        ai_alpha = np.asarray(result.convert("RGBA").getchannel("A"), dtype=np.float32) / 255

    red, green, blue = np.moveaxis(rgb, -1, 0)
    red_signal = red - np.maximum(green, blue)
    red_alpha = smoothstep(red_signal, 8, 48)

    lowest_channel = np.minimum(np.minimum(red, green), blue)
    saturation = np.maximum(np.maximum(red, green), blue) - lowest_channel
    white_alpha = smoothstep(lowest_channel, 180, 238) * (1 - smoothstep(saturation, 12, 42))
    color_alpha = np.maximum(red_alpha, white_alpha)

    # U²-Net can mistake the baked-in checkerboard for chip material; use its
    # confidence only to soften color-keyed antialiasing without losing details.
    alpha = color_alpha * (0.8 + 0.2 * ai_alpha)
    is_red = red_alpha >= white_alpha
    confident_red = rgb[red_alpha > 0.9]
    red_color = np.median(confident_red, axis=0) if confident_red.size else np.array([190, 25, 30])
    foreground = np.where(is_red[..., None], red_color, np.array([245, 245, 245]))
    rgba = np.dstack((np.clip(foreground, 0, 255), np.clip(alpha * 255, 0, 255)))
    logo = Image.fromarray(rgba.astype(np.uint8), mode="RGBA")

    bounds = logo.getchannel("A").getbbox()
    if bounds is None:
        raise RuntimeError("The background-removal model found no logo foreground.")
    logo = logo.crop(bounds)

    side = max(logo.size) * scale
    content_side = round(side * 0.88)
    resized = logo.convert("RGBa").resize(
        (
            max(1, round(logo.width * content_side / max(logo.size))),
            max(1, round(logo.height * content_side / max(logo.size))),
        ),
        Image.Resampling.LANCZOS,
    ).convert("RGBA")
    resized = resized.filter(ImageFilter.UnsharpMask(radius=0.8, percent=35, threshold=3))

    master = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    master.alpha_composite(
        resized,
        ((side - resized.width) // 2, (side - resized.height) // 2),
    )

    output.parent.mkdir(parents=True, exist_ok=True)
    master.save(output, format="PNG", optimize=True, dpi=(300, 300))
    print(f"Created transparent master: {output} ({side}x{side})")


def smoothstep(values: np.ndarray, low: float, high: float) -> np.ndarray:
    normalized = np.clip((values - low) / (high - low), 0, 1)
    return normalized * normalized * (3 - 2 * normalized)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--scale",
        type=int,
        default=4,
        help="High-quality Lanczos enlargement factor after AI background removal (default: 4).",
    )
    arguments = parser.parse_args()
    process(arguments.source, arguments.output, arguments.scale)


if __name__ == "__main__":
    main()

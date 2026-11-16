#!/usr/bin/env python3
"""Generate final header and favicon files from the transparent logo master."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageColor, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "assets" / "final" / "logo-master.png"
DEFAULT_OUTPUT = ROOT / "assets" / "final"
PNG_SIZES = (16, 32, 48, 64, 128, 256)
ICO_SIZES = ((16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256))
TILE_COLORS = {"dark": "#252528", "light": "#E8E8EA"}


def make_light_variant(source: Image.Image) -> Image.Image:
    pixels = np.asarray(source.convert("RGBA")).copy()
    red = (pixels[:, :, 0] > pixels[:, :, 1] * 1.35) & (
        pixels[:, :, 0] > pixels[:, :, 2] * 1.35
    )
    visible = pixels[:, :, 3] > 0
    dark_details = visible & ~red
    pixels[dark_details, :3] = (32, 32, 36)
    return Image.fromarray(pixels, mode="RGBA")


def render_square(source: Image.Image, size: int, margin: float = 0.08) -> Image.Image:
    content_size = round(size * (1 - 2 * margin))
    mark = source.convert("RGBA")
    mark.thumbnail((content_size, content_size), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(
        mark,
        ((size - mark.width) // 2, (size - mark.height) // 2),
    )
    return canvas


def render_tiled_square(
    source: Image.Image,
    size: int,
    tile_color: str,
    margin: float = 0.11,
) -> Image.Image:
    scale = 4
    scaled_size = size * scale
    canvas = Image.new("RGBA", (scaled_size, scaled_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle(
        (0, 0, scaled_size - 1, scaled_size - 1),
        radius=round(scaled_size * 0.22),
        fill=ImageColor.getcolor(tile_color, "RGBA"),
    )

    mark = source.convert("RGBA")
    content_size = round(scaled_size * (1 - 2 * margin))
    mark.thumbnail((content_size, content_size), Image.Resampling.LANCZOS)
    canvas.alpha_composite(
        mark,
        ((scaled_size - mark.width) // 2, (scaled_size - mark.height) // 2),
    )
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def build(source_path: Path, output_directory: Path) -> None:
    if not source_path.is_file():
        raise FileNotFoundError(
            f"Transparent logo master not found: {source_path}. "
            "Run process_logo.py first."
        )

    output_directory.mkdir(parents=True, exist_ok=True)
    with Image.open(source_path) as source:
        master = source.convert("RGBA")
        light_master = make_light_variant(master)
        light_master.save(output_directory / "logo-master-light.png", optimize=True)

        for theme, mark in (("dark", master), ("light", light_master)):
            target = output_directory / f"logo-header-{theme}.png"
            render_square(mark, 256).save(target, format="PNG", optimize=True)
            print(f"Created {target} (256x256)")

            tile_target = output_directory / f"logo-header-tile-{theme}.png"
            render_tiled_square(mark, 256, TILE_COLORS[theme]).save(
                tile_target, format="PNG", optimize=True
            )
            print(f"Created {tile_target} (256x256)")

        for size in PNG_SIZES:
            target = output_directory / f"favicon-{size}.png"
            render_square(light_master, size, margin=0.04).save(
                target, format="PNG", optimize=True
            )
            print(f"Created {target} ({size}x{size})")

        render_square(light_master, 256, margin=0.04).save(
            output_directory / "favicon.ico",
            format="ICO",
            sizes=ICO_SIZES,
        )
        print(f"Created {output_directory / 'favicon.ico'}")
        render_tiled_square(master, 256, TILE_COLORS["dark"]).save(
            output_directory / "app-icon.ico",
            format="ICO",
            sizes=ICO_SIZES,
        )
        print(f"Created {output_directory / 'app-icon.ico'}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    arguments = parser.parse_args()
    build(arguments.source, arguments.output_dir)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Build a raster-density PMTiles archive from a 2bulu road package."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import sqlite3
import struct
import sys
import time
import zipfile
import zlib
from collections import Counter
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Iterable, Iterator

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from pmtiles.tile import Compression, TileType, zxy_to_tileid
from pmtiles.writer import write


DEFAULT_BOUNDS = (115.9, 39.9, 116.0, 40.0)
DEFAULT_PASSWORD = "A89A8AD7B25F8A743A0D55E97556695"
WEB_MERCATOR_MAX_LATITUDE = 85.05112878
EARTH_CIRCUMFERENCE_METERS = 40_075_016.68557849
STYLE_VERSION = 3


@dataclass(slots=True)
class Shard:
    name: str
    bounds: tuple[float, float, float, float]
    record_count: int


@dataclass(slots=True)
class Shape:
    parts: list[list[tuple[float, float]]]
    bounds: tuple[float, float, float, float]


@dataclass(slots=True)
class Track:
    original_id: int
    parts: list[list[tuple[float, float]]] = field(default_factory=list)
    part_bounds: list[tuple[float, float, float, float]] = field(default_factory=list)
    pcount: int = 0
    speed_types: Counter[int] = field(default_factory=Counter)
    shape_length_meters: float = 0.0
    bounds: tuple[float, float, float, float] | None = None

    def add(self, shape: Shape, attributes: dict[str, int | float | str | None]) -> None:
        self.parts.extend(shape.parts)
        if len(shape.parts) == 1:
            self.part_bounds.append(shape.bounds)
        else:
            for part in shape.parts:
                self.part_bounds.append(
                    (
                        min(point[0] for point in part),
                        min(point[1] for point in part),
                        max(point[0] for point in part),
                        max(point[1] for point in part),
                    )
                )
        self.pcount = max(self.pcount, int(attributes.get("PCOUNT") or 0))
        speed_type = attributes.get("SPEEDTYPE")
        if isinstance(speed_type, int):
            self.speed_types[speed_type] += 1
        self.shape_length_meters = max(
            self.shape_length_meters, float(attributes.get("Shape_Leng") or 0.0)
        )
        self.bounds = union_bounds(self.bounds, shape.bounds)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build a bounded 2bulu trajectory-density PMTiles archive."
    )
    parser.add_argument("input", type=Path, help="Province ZIP package, for example Beijing.zip")
    parser.add_argument("output", type=Path, help="Output .pmtiles path")
    parser.add_argument(
        "--bounds",
        type=parse_bounds,
        default=DEFAULT_BOUNDS,
        metavar="WEST,SOUTH,EAST,NORTH",
        help="Experiment bounds in WGS84 (default: 115.9,39.9,116.0,40.0)",
    )
    parser.add_argument("--minzoom", type=int, default=11)
    parser.add_argument("--maxzoom", type=int, default=17)
    parser.add_argument("--tile-size", type=int, default=256, choices=(256, 512))
    parser.add_argument("--kernel-meters", type=float, default=4.0)
    parser.add_argument("--metatile-size", type=int, default=16)
    parser.add_argument("--tile-format", choices=("png", "webp"), default="webp")
    parser.add_argument("--max-records", type=int, default=20_000)
    parser.add_argument("--password", default=DEFAULT_PASSWORD)
    parser.add_argument("--name", default="Mentougou historical trajectory density experiment")
    parser.add_argument(
        "--preview",
        type=Path,
        help="Optional PNG preview path; uses the highest zoom that fits a manageable mosaic",
    )
    args = parser.parse_args()
    if args.minzoom < 0 or args.maxzoom < args.minzoom or args.maxzoom > 22:
        parser.error("zoom range must satisfy 0 <= minzoom <= maxzoom <= 22")
    if args.kernel_meters <= 0:
        parser.error("--kernel-meters must be positive")
    if args.metatile_size <= 0 or args.metatile_size > 32:
        parser.error("--metatile-size must be between 1 and 32")
    if args.max_records <= 0:
        parser.error("--max-records must be positive")
    return args


def parse_bounds(value: str) -> tuple[float, float, float, float]:
    try:
        west, south, east, north = (float(part.strip()) for part in value.split(","))
    except (TypeError, ValueError) as exc:
        raise argparse.ArgumentTypeError("bounds must contain four comma-separated numbers") from exc
    if not (-180 <= west < east <= 180 and -90 <= south < north <= 90):
        raise argparse.ArgumentTypeError("bounds must be ordered WGS84 coordinates")
    return west, south, east, north


def union_bounds(
    first: tuple[float, float, float, float] | None,
    second: tuple[float, float, float, float],
) -> tuple[float, float, float, float]:
    if first is None:
        return second
    return (
        min(first[0], second[0]),
        min(first[1], second[1]),
        max(first[2], second[2]),
        max(first[3], second[3]),
    )


def intersects(
    first: tuple[float, float, float, float],
    second: tuple[float, float, float, float],
) -> bool:
    # Shapefile grid edges carry sub-nanodegree rounding noise. Requiring a tiny
    # positive overlap keeps edge-touching neighbor shards out of the selection.
    epsilon = 1e-8
    return (
        min(first[2], second[2]) - max(first[0], second[0]) > epsilon
        and min(first[3], second[3]) - max(first[1], second[1]) > epsilon
    )


def geometry_intersects(
    first: tuple[float, float, float, float],
    second: tuple[float, float, float, float],
) -> bool:
    return not (
        first[2] < second[0]
        or first[0] > second[2]
        or first[3] < second[1]
        or first[1] > second[3]
    )


def open_inner_archive(outer: zipfile.ZipFile, name: str) -> zipfile.ZipFile:
    return zipfile.ZipFile(io.BytesIO(outer.read(name)))


def read_prefix(inner: zipfile.ZipFile, name: str, password: bytes, length: int) -> bytes:
    with inner.open(name, pwd=password) as stream:
        return stream.read(length)


def discover_shards(
    package: Path,
    password: bytes,
    requested_bounds: tuple[float, float, float, float],
) -> list[Shard]:
    selected: list[Shard] = []
    with zipfile.ZipFile(package) as outer:
        for outer_entry in outer.infolist():
            if not outer_entry.filename.lower().endswith(".zip"):
                continue
            with open_inner_archive(outer, outer_entry.filename) as inner:
                shp_header = read_prefix(inner, "trackpos.shp", password, 100)
                dbf_header = read_prefix(inner, "trackpos.dbf", password, 32)
            if len(shp_header) != 100 or struct.unpack_from(">I", shp_header, 0)[0] != 9994:
                raise ValueError(f"{outer_entry.filename}: invalid Shapefile header")
            shape_type = struct.unpack_from("<I", shp_header, 32)[0]
            if shape_type != 13:
                raise ValueError(f"{outer_entry.filename}: expected PolyLineZ, got type {shape_type}")
            bounds = struct.unpack_from("<4d", shp_header, 36)
            record_count = struct.unpack_from("<I", dbf_header, 4)[0]
            if intersects(bounds, requested_bounds):
                selected.append(Shard(outer_entry.filename, bounds, record_count))
    return selected


def parse_dbf(data: bytes) -> list[dict[str, int | float | str | None] | None]:
    record_count = struct.unpack_from("<I", data, 4)[0]
    header_length, record_length = struct.unpack_from("<HH", data, 8)
    fields: list[tuple[str, str, int, int]] = []
    offset = 32
    while offset + 32 <= header_length and data[offset] != 0x0D:
        descriptor = data[offset : offset + 32]
        name = descriptor[:11].split(b"\0", 1)[0].decode("ascii")
        fields.append((name, chr(descriptor[11]), descriptor[16], descriptor[17]))
        offset += 32

    rows: list[dict[str, int | float | str | None] | None] = []
    offset = header_length
    for _ in range(record_count):
        record = data[offset : offset + record_length]
        offset += record_length
        if len(record) != record_length:
            raise ValueError("truncated DBF record")
        if record[0:1] == b"*":
            rows.append(None)
            continue
        values: dict[str, int | float | str | None] = {}
        cursor = 1
        for name, field_type, width, decimals in fields:
            text = record[cursor : cursor + width].decode("utf-8", errors="replace").strip(" \0")
            cursor += width
            if not text:
                value: int | float | str | None = None
            elif field_type in ("N", "F"):
                value = float(text) if decimals else int(text)
            else:
                value = text
            values[name] = value
        rows.append(values)
    return rows


def parse_polylinez(data: bytes) -> list[Shape | None]:
    if len(data) < 100 or struct.unpack_from(">I", data, 0)[0] != 9994:
        raise ValueError("invalid Shapefile")
    shapes: list[Shape | None] = []
    offset = 100
    while offset + 8 <= len(data):
        _, content_words = struct.unpack_from(">II", data, offset)
        offset += 8
        content_length = content_words * 2
        content = data[offset : offset + content_length]
        offset += content_length
        if len(content) != content_length:
            raise ValueError("truncated Shapefile record")
        shape_type = struct.unpack_from("<I", content, 0)[0]
        if shape_type == 0:
            shapes.append(None)
            continue
        if shape_type != 13 or len(content) < 44:
            raise ValueError(f"unsupported shape type {shape_type}")
        bounds = struct.unpack_from("<4d", content, 4)
        part_count, point_count = struct.unpack_from("<II", content, 36)
        part_starts = list(struct.unpack_from(f"<{part_count}I", content, 44))
        points_offset = 44 + part_count * 4
        points = [
            struct.unpack_from("<2d", content, points_offset + index * 16)
            for index in range(point_count)
        ]
        ends = part_starts[1:] + [point_count]
        parts = [points[start:end] for start, end in zip(part_starts, ends) if end - start >= 2]
        shapes.append(Shape(parts, bounds) if parts else None)
    return shapes


def load_tracks(
    package: Path,
    selected_shards: Iterable[Shard],
    password: bytes,
) -> dict[int, Track]:
    tracks: dict[int, Track] = {}
    shard_names = {shard.name for shard in selected_shards}
    with zipfile.ZipFile(package) as outer:
        for shard_name in sorted(shard_names):
            print(f"Reading {shard_name} ...", flush=True)
            with open_inner_archive(outer, shard_name) as inner:
                attributes = parse_dbf(inner.read("trackpos.dbf", pwd=password))
                shapes = parse_polylinez(inner.read("trackpos.shp", pwd=password))
            if len(attributes) != len(shapes):
                raise ValueError(
                    f"{shard_name}: DBF has {len(attributes)} rows but SHP has {len(shapes)} records"
                )
            for shape, row in zip(shapes, attributes):
                if shape is None or row is None or int(row.get("PCOUNT") or 0) <= 10:
                    continue
                original_id = int(row.get("ORIGINALID") or row.get("PID") or 0)
                if not original_id:
                    continue
                track = tracks.setdefault(original_id, Track(original_id))
                track.add(shape, row)
    return tracks


def lon_to_global_pixel(lon: float, zoom: int, tile_size: int) -> float:
    return (lon + 180.0) / 360.0 * (1 << zoom) * tile_size


def lat_to_global_pixel(lat: float, zoom: int, tile_size: int) -> float:
    lat = max(-WEB_MERCATOR_MAX_LATITUDE, min(WEB_MERCATOR_MAX_LATITUDE, lat))
    sine = math.sin(math.radians(lat))
    normalized = 0.5 - math.log((1 + sine) / (1 - sine)) / (4 * math.pi)
    return normalized * (1 << zoom) * tile_size


def tile_range_for_bounds(
    bounds: tuple[float, float, float, float],
    zoom: int,
    tile_size: int,
    buffer_pixels: int = 0,
) -> tuple[int, int, int, int]:
    west, south, east, north = bounds
    minimum_x = math.floor((lon_to_global_pixel(west, zoom, tile_size) - buffer_pixels) / tile_size)
    maximum_x = math.floor(
        (lon_to_global_pixel(math.nextafter(east, west), zoom, tile_size) + buffer_pixels) / tile_size
    )
    minimum_y = math.floor((lat_to_global_pixel(north, zoom, tile_size) - buffer_pixels) / tile_size)
    maximum_y = math.floor(
        (lat_to_global_pixel(math.nextafter(south, north), zoom, tile_size) + buffer_pixels) / tile_size
    )
    limit = (1 << zoom) - 1
    return (
        max(0, min(limit, minimum_x)),
        max(0, min(limit, minimum_y)),
        max(0, min(limit, maximum_x)),
        max(0, min(limit, maximum_y)),
    )


def tile_x_to_longitude(x: int, zoom: int) -> float:
    return x / (1 << zoom) * 360.0 - 180.0


def tile_y_to_latitude(y: int, zoom: int) -> float:
    mercator = math.pi * (1.0 - 2.0 * y / (1 << zoom))
    return math.degrees(math.atan(math.sinh(mercator)))


def bounds_for_tile_range(
    minimum_x: int,
    minimum_y: int,
    maximum_x: int,
    maximum_y: int,
    zoom: int,
) -> tuple[float, float, float, float]:
    return (
        tile_x_to_longitude(minimum_x, zoom),
        tile_y_to_latitude(maximum_y + 1, zoom),
        tile_x_to_longitude(maximum_x + 1, zoom),
        tile_y_to_latitude(minimum_y, zoom),
    )


def iter_metatile_ranges(
    tile_range: tuple[int, int, int, int],
    metatile_size: int,
) -> Iterator[tuple[int, int, int, int]]:
    minimum_x, minimum_y, maximum_x, maximum_y = tile_range
    for y in range(minimum_y, maximum_y + 1, metatile_size):
        for x in range(minimum_x, maximum_x + 1, metatile_size):
            yield (
                x,
                y,
                min(maximum_x, x + metatile_size - 1),
                min(maximum_y, y + metatile_size - 1),
            )


def build_track_spatial_index(
    tracks: list[Track],
    zoom: int,
    tile_size: int,
    output_bounds: tuple[float, float, float, float],
) -> dict[tuple[int, int], list[int]]:
    index: dict[tuple[int, int], list[int]] = {}
    for track_index, track in enumerate(tracks):
        if (
            track.bounds is None
            or not intersects(track.bounds, output_bounds)
            or not track.part_bounds
        ):
            continue
        cells: set[tuple[int, int]] = set()
        for part_bounds in track.part_bounds:
            minimum_x, minimum_y, maximum_x, maximum_y = tile_range_for_bounds(
                part_bounds, zoom, tile_size
            )
            maximum_x = max(minimum_x, maximum_x)
            maximum_y = max(minimum_y, maximum_y)
            for x in range(minimum_x, maximum_x + 1):
                for y in range(minimum_y, maximum_y + 1):
                    cells.add((x, y))
        for cell in cells:
            index.setdefault(cell, []).append(track_index)
    return index


def tracks_for_tile_range(
    spatial_index: dict[tuple[int, int], list[int]],
    tile_range: tuple[int, int, int, int],
    zoom: int,
    index_zoom: int,
) -> list[int]:
    shift = zoom - index_zoom
    minimum_x, minimum_y, maximum_x, maximum_y = tile_range
    parent_minimum_x = minimum_x >> shift
    parent_minimum_y = minimum_y >> shift
    parent_maximum_x = maximum_x >> shift
    parent_maximum_y = maximum_y >> shift
    candidates: set[int] = set()
    for x in range(parent_minimum_x, parent_maximum_x + 1):
        for y in range(parent_minimum_y, parent_maximum_y + 1):
            candidates.update(spatial_index.get((x, y), ()))
    return sorted(candidates)


def rasterize_density_tile_range(
    tracks: list[Track],
    track_indexes: Iterable[int],
    tile_range: tuple[int, int, int, int],
    zoom: int,
    tile_size: int,
) -> np.ndarray:
    minimum_x, minimum_y, maximum_x, maximum_y = tile_range
    width = (maximum_x - minimum_x + 1) * tile_size
    height = (maximum_y - minimum_y + 1) * tile_size
    counts = np.zeros((height, width), dtype=np.uint16)
    origin_x = minimum_x * tile_size
    origin_y = minimum_y * tile_size
    output_bounds = bounds_for_tile_range(*tile_range, zoom)

    for track_index in track_indexes:
        track = tracks[track_index]
        if track.bounds is None or not geometry_intersects(track.bounds, output_bounds):
            continue
        projected_parts: list[list[tuple[float, float]]] = []
        left = width
        top = height
        right = -1
        bottom = -1
        for part, part_bounds in zip(track.parts, track.part_bounds):
            if not geometry_intersects(part_bounds, output_bounds):
                continue
            pixels = [
                (
                    lon_to_global_pixel(lon, zoom, tile_size) - origin_x,
                    lat_to_global_pixel(lat, zoom, tile_size) - origin_y,
                )
                for lon, lat in part
            ]
            if len(pixels) < 2:
                continue
            projected_parts.append(pixels)
            left = min(left, math.floor(min(point[0] for point in pixels)) - 1)
            top = min(top, math.floor(min(point[1] for point in pixels)) - 1)
            right = max(right, math.ceil(max(point[0] for point in pixels)) + 1)
            bottom = max(bottom, math.ceil(max(point[1] for point in pixels)) + 1)
        left = max(0, left)
        top = max(0, top)
        right = min(width - 1, right)
        bottom = min(height - 1, bottom)
        if not projected_parts or right < left or bottom < top:
            continue
        mask = Image.new("1", (right - left + 1, bottom - top + 1))
        draw = ImageDraw.Draw(mask)
        for pixels in projected_parts:
            draw.line([(x - left, y - top) for x, y in pixels], fill=1, width=1, joint="curve")
        counts[top : bottom + 1, left : right + 1] += np.asarray(mask, dtype=np.uint16)
    return counts


def rasterize_density_surface(
    tracks: list[Track],
    output_bounds: tuple[float, float, float, float],
    zoom: int,
    tile_size: int,
    buffer_pixels: int,
) -> tuple[np.ndarray, tuple[int, int, int, int]]:
    minimum_x, minimum_y, maximum_x, maximum_y = tile_range_for_bounds(
        output_bounds, zoom, tile_size
    )
    width = (maximum_x - minimum_x + 1) * tile_size + buffer_pixels * 2
    height = (maximum_y - minimum_y + 1) * tile_size + buffer_pixels * 2
    counts = np.zeros((height, width), dtype=np.uint16)
    origin_x = minimum_x * tile_size - buffer_pixels
    origin_y = minimum_y * tile_size - buffer_pixels
    for track_number, track in enumerate(tracks, start=1):
        if track.bounds is None or not intersects(track.bounds, output_bounds):
            continue
        projected_parts: list[list[tuple[float, float]]] = []
        left = width
        top = height
        right = -1
        bottom = -1
        for part in track.parts:
            pixels = [
                (
                    lon_to_global_pixel(lon, zoom, tile_size) - origin_x,
                    lat_to_global_pixel(lat, zoom, tile_size) - origin_y,
                )
                for lon, lat in part
            ]
            if len(pixels) < 2:
                continue
            projected_parts.append(pixels)
            left = min(left, math.floor(min(point[0] for point in pixels)) - 1)
            top = min(top, math.floor(min(point[1] for point in pixels)) - 1)
            right = max(right, math.ceil(max(point[0] for point in pixels)) + 1)
            bottom = max(bottom, math.ceil(max(point[1] for point in pixels)) + 1)
        left = max(0, left)
        top = max(0, top)
        right = min(width - 1, right)
        bottom = min(height - 1, bottom)
        if not projected_parts or right < left or bottom < top:
            continue
        mask = Image.new("1", (right - left + 1, bottom - top + 1))
        draw = ImageDraw.Draw(mask)
        for pixels in projected_parts:
            draw.line([(x - left, y - top) for x, y in pixels], fill=1, width=1, joint="curve")
        counts[top : bottom + 1, left : right + 1] += np.asarray(mask, dtype=np.uint16)
        if track_number % 500 == 0:
            print(f"  rasterized {track_number}/{len(tracks)} tracks", flush=True)
    return counts, (minimum_x, minimum_y, maximum_x, maximum_y)


def percentile_from_histogram(histogram: np.ndarray, percentile: float) -> int:
    nonzero_total = int(histogram[1:].sum())
    if nonzero_total == 0:
        return 1
    target = math.ceil(nonzero_total * percentile)
    cumulative = np.cumsum(histogram[1:])
    return int(np.searchsorted(cumulative, target) + 1)


def meters_per_pixel(latitude: float, zoom: int, tile_size: int) -> float:
    return (
        math.cos(math.radians(latitude))
        * EARTH_CIRCUMFERENCE_METERS
        / ((1 << zoom) * tile_size)
    )


def colorize_density(
    counts: np.ndarray,
    p99: int,
    blur_radius: float,
    tile_size: int,
    buffer_pixels: int,
) -> Image.Image:
    normalized = np.log1p(counts.astype(np.float32)) / math.log1p(max(1, p99))
    normalized = np.clip(normalized, 0.0, 1.0) ** 0.86
    intensity = Image.fromarray(np.round(normalized * 255).astype(np.uint8), "L")
    blurred = intensity.filter(ImageFilter.GaussianBlur(radius=blur_radius))
    detail = intensity.filter(ImageFilter.GaussianBlur(radius=0.65))
    blurred_values = np.asarray(blurred, dtype=np.float32) / 255.0
    detail_values = np.asarray(detail, dtype=np.float32) / 255.0
    values = np.maximum(blurred_values * 0.62, detail_values * 0.98)
    values[values < 0.04] = 0.0

    low = np.array([23.0, 64.0, 107.0], dtype=np.float32)
    middle = np.array([0.0, 164.0, 193.0], dtype=np.float32)
    high = np.array([148.0, 230.0, 92.0], dtype=np.float32)
    first_mix = np.clip(values / 0.42, 0.0, 1.0)[..., None]
    second_mix = np.clip((values - 0.42) / 0.58, 0.0, 1.0)[..., None]
    colors = low * (1.0 - first_mix) + middle * first_mix
    colors = colors * (1.0 - second_mix) + high * second_mix
    alpha = np.round((np.clip(values * 1.28, 0.0, 1.0) ** 0.72) * 245).astype(np.uint8)
    rgba = np.dstack((np.round(colors).astype(np.uint8), alpha))
    image = Image.fromarray(rgba, "RGBA")
    return image.crop(
        (
            buffer_pixels,
            buffer_pixels,
            buffer_pixels + tile_size,
            buffer_pixels + tile_size,
        )
    )


def image_to_png(image: Image.Image) -> bytes:
    return image_to_tile(image, "png")


def image_to_tile(image: Image.Image, tile_format: str) -> bytes:
    output = io.BytesIO()
    if tile_format == "webp":
        image.save(output, format="WEBP", lossless=True, method=4, exact=True)
    else:
        image.save(output, format="PNG", compress_level=6)
    return output.getvalue()


def build_signature(
    package: Path,
    shards: list[Shard],
    bounds: tuple[float, float, float, float],
    minzoom: int,
    maxzoom: int,
    tile_size: int,
    kernel_meters: float,
    metatile_size: int,
    tile_format: str,
) -> str:
    source = package.stat()
    payload = {
        "source": str(package.resolve()),
        "source_size": source.st_size,
        "source_mtime_ns": source.st_mtime_ns,
        "shards": [shard.name for shard in shards],
        "bounds": bounds,
        "minzoom": minzoom,
        "maxzoom": maxzoom,
        "tile_size": tile_size,
        "kernel_meters": kernel_meters,
        "metatile_size": metatile_size,
        "tile_format": tile_format,
        "style_version": STYLE_VERSION,
    }
    encoded = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def open_build_database(path: Path, signature: str) -> sqlite3.Connection:
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA journal_mode=OFF")
    connection.execute("PRAGMA synchronous=OFF")
    connection.execute("PRAGMA locking_mode=EXCLUSIVE")
    connection.execute("PRAGMA temp_store=MEMORY")
    connection.execute(
        "CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID"
    )
    connection.execute(
        "CREATE TABLE IF NOT EXISTS counts (x INTEGER NOT NULL, y INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY (x, y)) WITHOUT ROWID"
    )
    connection.execute(
        "CREATE TABLE IF NOT EXISTS tiles (tile_id INTEGER PRIMARY KEY, zoom INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, data BLOB NOT NULL)"
    )
    connection.execute(
        "CREATE INDEX IF NOT EXISTS tiles_zoom_xy ON tiles (zoom, x, y)"
    )
    connection.execute(
        "CREATE TABLE IF NOT EXISTS completed_zooms (zoom INTEGER PRIMARY KEY, p99 INTEGER NOT NULL, tile_count INTEGER NOT NULL)"
    )
    row = connection.execute(
        "SELECT value FROM metadata WHERE key = 'signature'"
    ).fetchone()
    if row is None or row[0] != signature:
        print("Starting a new chunked tile cache.", flush=True)
        connection.execute("DELETE FROM counts")
        connection.execute("DELETE FROM tiles")
        connection.execute("DELETE FROM completed_zooms")
        connection.execute("DELETE FROM metadata")
        connection.execute(
            "INSERT INTO metadata (key, value) VALUES ('signature', ?)", (signature,)
        )
        connection.commit()
    else:
        completed = [
            str(row[0])
            for row in connection.execute("SELECT zoom FROM completed_zooms ORDER BY zoom")
        ]
        if completed:
            print(f"Resuming tile cache; completed zooms: {', '.join(completed)}", flush=True)
    return connection


def buffered_counts_from_database(
    load_tile,
    x: int,
    y: int,
    tile_size: int,
    buffer_pixels: int,
) -> np.ndarray:
    size = tile_size + buffer_pixels * 2
    result = np.zeros((size, size), dtype=np.uint16)
    for offset_y in (-1, 0, 1):
        for offset_x in (-1, 0, 1):
            tile = load_tile(x + offset_x, y + offset_y)
            if tile is None:
                continue
            destination_left = buffer_pixels + offset_x * tile_size
            destination_top = buffer_pixels + offset_y * tile_size
            destination_right = destination_left + tile_size
            destination_bottom = destination_top + tile_size
            clipped_left = max(0, destination_left)
            clipped_top = max(0, destination_top)
            clipped_right = min(size, destination_right)
            clipped_bottom = min(size, destination_bottom)
            if clipped_left >= clipped_right or clipped_top >= clipped_bottom:
                continue
            source_left = clipped_left - destination_left
            source_top = clipped_top - destination_top
            source_right = source_left + clipped_right - clipped_left
            source_bottom = source_top + clipped_bottom - clipped_top
            result[clipped_top:clipped_bottom, clipped_left:clipped_right] = tile[
                source_top:source_bottom, source_left:source_right
            ]
    return result


def generate_tiles_chunked(
    tracks: list[Track],
    bounds: tuple[float, float, float, float],
    minzoom: int,
    maxzoom: int,
    tile_size: int,
    kernel_meters: float,
    metatile_size: int,
    tile_format: str,
    connection: sqlite3.Connection,
) -> tuple[dict[int, int], int]:
    center_latitude = (bounds[1] + bounds[3]) / 2
    spatial_index = build_track_spatial_index(tracks, minzoom, tile_size, bounds)
    print(
        f"Spatial index: {len(spatial_index)} z{minzoom} cells for {len(tracks)} tracks.",
        flush=True,
    )
    p99_by_zoom: dict[int, int] = {}

    for zoom in range(minzoom, maxzoom + 1):
        completed = connection.execute(
            "SELECT p99, tile_count FROM completed_zooms WHERE zoom = ?", (zoom,)
        ).fetchone()
        if completed is not None:
            p99_by_zoom[zoom] = int(completed[0])
            print(f"Skipping completed z{zoom}: {completed[1]} tiles, p99={completed[0]}.", flush=True)
            continue

        zoom_tile_range = tile_range_for_bounds(bounds, zoom, tile_size)
        metatiles = list(iter_metatile_ranges(zoom_tile_range, metatile_size))
        blur_radius = max(
            0.35,
            min(2.4, kernel_meters / meters_per_pixel(center_latitude, zoom, tile_size)),
        )
        buffer_pixels = math.ceil(blur_radius * 3) + 2
        first_tile_id = (4**zoom - 1) // 3
        last_tile_id = first_tile_id + 4**zoom - 1
        connection.execute("DELETE FROM counts")
        connection.execute(
            "DELETE FROM tiles WHERE tile_id BETWEEN ? AND ?", (first_tile_id, last_tile_id)
        )
        connection.commit()

        print(
            f"Rasterizing z{zoom}: {len(metatiles)} metatiles, blur={blur_radius:.2f}px ...",
            flush=True,
        )
        histogram = np.zeros(65_536, dtype=np.int64)
        nonempty_tiles = 0
        started = time.monotonic()
        progress_interval = max(1, len(metatiles) // 20)
        for metatile_number, metatile_range in enumerate(metatiles, start=1):
            minimum_x, minimum_y, maximum_x, maximum_y = metatile_range
            tile_limit = (1 << zoom) - 1
            render_range = (
                max(0, minimum_x - 1),
                max(0, minimum_y - 1),
                min(tile_limit, maximum_x + 1),
                min(tile_limit, maximum_y + 1),
            )
            candidate_indexes = tracks_for_tile_range(
                spatial_index, render_range, zoom, minzoom
            )
            surface = rasterize_density_tile_range(
                tracks, candidate_indexes, render_range, zoom, tile_size
            )
            render_minimum_x, render_minimum_y, _, _ = render_range
            core_left = (minimum_x - render_minimum_x) * tile_size
            core_top = (minimum_y - render_minimum_y) * tile_size
            core = surface[
                core_top : core_top + (maximum_y - minimum_y + 1) * tile_size,
                core_left : core_left + (maximum_x - minimum_x + 1) * tile_size,
            ]
            histogram += np.bincount(core.ravel(), minlength=65_536)[:65_536]
            with connection:
                for x in range(minimum_x, maximum_x + 1):
                    for y in range(minimum_y, maximum_y + 1):
                        local_x = (x - render_minimum_x) * tile_size
                        local_y = (y - render_minimum_y) * tile_size
                        tile = np.ascontiguousarray(
                            surface[
                                local_y : local_y + tile_size,
                                local_x : local_x + tile_size,
                            ]
                        )
                        if not np.any(tile):
                            continue
                        compressed = zlib.compress(tile.tobytes(), level=1)
                        connection.execute(
                            "INSERT OR REPLACE INTO counts (x, y, data) VALUES (?, ?, ?)",
                            (x, y, sqlite3.Binary(compressed)),
                        )
                        nonempty_tiles += 1
            if metatile_number % progress_interval == 0 or metatile_number == len(metatiles):
                elapsed = time.monotonic() - started
                print(
                    f"  z{zoom} density {metatile_number}/{len(metatiles)} "
                    f"({metatile_number / len(metatiles):.0%}), {elapsed:.0f}s",
                    flush=True,
                )
            del surface

        p99 = percentile_from_histogram(histogram, 0.99)
        p99_by_zoom[zoom] = p99
        print(
            f"Colorizing z{zoom}: p99={p99}, {nonempty_tiles} non-empty tiles ...",
            flush=True,
        )

        @lru_cache(maxsize=256)
        def load_count_tile(x: int, y: int) -> np.ndarray | None:
            row = connection.execute(
                "SELECT data FROM counts WHERE x = ? AND y = ?", (x, y)
            ).fetchone()
            if row is None:
                return None
            return np.frombuffer(zlib.decompress(row[0]), dtype=np.uint16).reshape(
                (tile_size, tile_size)
            )

        coordinates = list(connection.execute("SELECT x, y FROM counts ORDER BY x, y"))
        progress_interval = max(1, len(coordinates) // 20)
        started = time.monotonic()
        rendered_count = 0
        for tile_number, (x, y) in enumerate(coordinates, start=1):
            counts = buffered_counts_from_database(
                load_count_tile, x, y, tile_size, buffer_pixels
            )
            image = colorize_density(counts, p99, blur_radius, tile_size, buffer_pixels)
            if image.getchannel("A").getbbox() is not None:
                connection.execute(
                    "INSERT OR REPLACE INTO tiles (tile_id, zoom, x, y, data) VALUES (?, ?, ?, ?, ?)",
                    (
                        zxy_to_tileid(zoom, x, y),
                        zoom,
                        x,
                        y,
                        sqlite3.Binary(image_to_tile(image, tile_format)),
                    ),
                )
                rendered_count += 1
            if tile_number % 100 == 0:
                connection.commit()
            if tile_number % progress_interval == 0 or tile_number == len(coordinates):
                elapsed = time.monotonic() - started
                print(
                    f"  z{zoom} {tile_format.upper()} {tile_number}/{len(coordinates)} "
                    f"({tile_number / len(coordinates):.0%}), {elapsed:.0f}s",
                    flush=True,
                )
        connection.execute(
            "INSERT OR REPLACE INTO completed_zooms (zoom, p99, tile_count) VALUES (?, ?, ?)",
            (zoom, p99, rendered_count),
        )
        connection.execute("DELETE FROM counts")
        connection.commit()
        load_count_tile.cache_clear()

    tile_count = int(connection.execute("SELECT COUNT(*) FROM tiles").fetchone()[0])
    return p99_by_zoom, tile_count


def generate_tiles(
    tracks: list[Track],
    bounds: tuple[float, float, float, float],
    minzoom: int,
    maxzoom: int,
    tile_size: int,
    kernel_meters: float,
) -> tuple[list[tuple[int, int, int, bytes]], dict[int, int]]:
    center_latitude = (bounds[1] + bounds[3]) / 2
    rendered: list[tuple[int, int, int, bytes]] = []
    p99_by_zoom: dict[int, int] = {}
    for zoom in range(minzoom, maxzoom + 1):
        blur_radius = max(
            0.35,
            min(2.4, kernel_meters / meters_per_pixel(center_latitude, zoom, tile_size)),
        )
        buffer_pixels = math.ceil(blur_radius * 3) + 2
        print(
            f"Rasterizing z{zoom}: {len(tracks)} tracks, blur={blur_radius:.2f}px ...",
            flush=True,
        )
        surface, tile_range = rasterize_density_surface(
            tracks, bounds, zoom, tile_size, buffer_pixels
        )
        core = surface[
            buffer_pixels : surface.shape[0] - buffer_pixels,
            buffer_pixels : surface.shape[1] - buffer_pixels,
        ]
        histogram = np.bincount(core.ravel(), minlength=65_536)
        p99 = percentile_from_histogram(histogram, 0.99)
        p99_by_zoom[zoom] = p99
        minimum_x, minimum_y, maximum_x, maximum_y = tile_range
        possible_tiles = (maximum_x - minimum_x + 1) * (maximum_y - minimum_y + 1)
        print(f"Colorizing z{zoom}: p99={p99}, possible tiles={possible_tiles} ...", flush=True)
        for x in range(minimum_x, maximum_x + 1):
            for y in range(minimum_y, maximum_y + 1):
                local_x = (x - minimum_x) * tile_size
                local_y = (y - minimum_y) * tile_size
                counts = surface[
                    local_y : local_y + tile_size + buffer_pixels * 2,
                    local_x : local_x + tile_size + buffer_pixels * 2,
                ]
                tile_core = counts[
                    buffer_pixels : buffer_pixels + tile_size,
                    buffer_pixels : buffer_pixels + tile_size,
                ]
                if not np.any(tile_core):
                    continue
                image = colorize_density(counts, p99, blur_radius, tile_size, buffer_pixels)
                if image.getchannel("A").getbbox() is None:
                    continue
                rendered.append((zoom, x, y, image_to_png(image)))
        del surface
    return rendered, p99_by_zoom


def write_pmtiles(
    output: Path,
    tiles: Iterable[tuple[int, int, int, bytes]],
    bounds: tuple[float, float, float, float],
    minzoom: int,
    maxzoom: int,
    name: str,
    tile_size: int,
    shard_names: list[str],
    track_count: int,
    p99_by_zoom: dict[int, int],
) -> None:
    west, south, east, north = bounds
    header = {
        "tile_type": TileType.PNG,
        "tile_compression": Compression.NONE,
        "min_zoom": minzoom,
        "max_zoom": maxzoom,
        "min_lon_e7": round(west * 10_000_000),
        "min_lat_e7": round(south * 10_000_000),
        "max_lon_e7": round(east * 10_000_000),
        "max_lat_e7": round(north * 10_000_000),
        "center_zoom": min(maxzoom, 13),
        "center_lon_e7": round((west + east) / 2 * 10_000_000),
        "center_lat_e7": round((south + north) / 2 * 10_000_000),
    }
    metadata = {
        "name": name,
        "description": "Distinct historical trajectories rasterized as a local density overlay.",
        "version": "2017-08-16",
        "type": "overlay",
        "format": "png",
        "bounds": ",".join(str(value) for value in bounds),
        "minzoom": minzoom,
        "maxzoom": maxzoom,
        "gpxview:tile_size": tile_size,
        "gpxview:source_shards": shard_names,
        "gpxview:distinct_tracks": track_count,
        "gpxview:p99_by_zoom": {str(key): value for key, value in p99_by_zoom.items()},
        "gpxview:density_measure": "distinct ORIGINALID crossings",
        "gpxview:data_date": "2017-08-16",
    }
    ordered = sorted(tiles, key=lambda tile: zxy_to_tileid(tile[0], tile[1], tile[2]))
    if not ordered:
        raise ValueError("no non-empty tiles were generated")
    output.parent.mkdir(parents=True, exist_ok=True)
    with write(str(output)) as writer:
        for zoom, x, y, data in ordered:
            writer.write_tile(zxy_to_tileid(zoom, x, y), data)
        writer.finalize(header, metadata)


def write_pmtiles_from_database(
    output: Path,
    connection: sqlite3.Connection,
    bounds: tuple[float, float, float, float],
    minzoom: int,
    maxzoom: int,
    name: str,
    tile_size: int,
    shard_names: list[str],
    track_count: int,
    p99_by_zoom: dict[int, int],
    tile_format: str,
) -> None:
    west, south, east, north = bounds
    header = {
        "tile_type": TileType.WEBP if tile_format == "webp" else TileType.PNG,
        "tile_compression": Compression.NONE,
        "min_zoom": minzoom,
        "max_zoom": maxzoom,
        "min_lon_e7": round(west * 10_000_000),
        "min_lat_e7": round(south * 10_000_000),
        "max_lon_e7": round(east * 10_000_000),
        "max_lat_e7": round(north * 10_000_000),
        "center_zoom": min(maxzoom, 11),
        "center_lon_e7": round((west + east) / 2 * 10_000_000),
        "center_lat_e7": round((south + north) / 2 * 10_000_000),
    }
    metadata = {
        "name": name,
        "description": "Distinct historical trajectories rasterized as a local density overlay.",
        "version": "2017-08-16",
        "type": "overlay",
        "format": tile_format,
        "bounds": ",".join(str(value) for value in bounds),
        "minzoom": minzoom,
        "maxzoom": maxzoom,
        "gpxview:tile_size": tile_size,
        "gpxview:source_shards": shard_names,
        "gpxview:distinct_tracks": track_count,
        "gpxview:p99_by_zoom": {str(key): value for key, value in p99_by_zoom.items()},
        "gpxview:density_measure": "distinct ORIGINALID crossings",
        "gpxview:data_date": "2017-08-16",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    partial = output.with_name(f"{output.name}.partial")
    with write(str(partial)) as writer:
        for tile_id, data in connection.execute("SELECT tile_id, data FROM tiles ORDER BY tile_id"):
            writer.write_tile(tile_id, data)
        writer.finalize(header, metadata)
    partial.replace(output)


def write_preview_from_database(
    preview_path: Path,
    connection: sqlite3.Connection,
    tile_size: int,
) -> None:
    zooms = [row[0] for row in connection.execute("SELECT DISTINCT zoom FROM tiles ORDER BY zoom DESC")]
    if not zooms:
        raise ValueError("no non-empty tiles were generated")
    selected_zoom = zooms[-1]
    selected_bounds: tuple[int, int, int, int] | None = None
    for zoom in zooms:
        row = connection.execute(
            "SELECT MIN(x), MIN(y), MAX(x), MAX(y) FROM tiles WHERE zoom = ?", (zoom,)
        ).fetchone()
        minimum_x, minimum_y, maximum_x, maximum_y = (int(value) for value in row)
        width = (maximum_x - minimum_x + 1) * tile_size
        height = (maximum_y - minimum_y + 1) * tile_size
        if width <= 4096 and height <= 4096:
            selected_zoom = zoom
            selected_bounds = (minimum_x, minimum_y, maximum_x, maximum_y)
            break
    if selected_bounds is None:
        row = connection.execute(
            "SELECT MIN(x), MIN(y), MAX(x), MAX(y) FROM tiles WHERE zoom = ?",
            (selected_zoom,),
        ).fetchone()
        selected_bounds = tuple(int(value) for value in row)
    minimum_x, minimum_y, maximum_x, maximum_y = selected_bounds
    mosaic = Image.new(
        "RGBA",
        ((maximum_x - minimum_x + 1) * tile_size, (maximum_y - minimum_y + 1) * tile_size),
    )
    for x, y, data in connection.execute(
        "SELECT x, y, data FROM tiles WHERE zoom = ? ORDER BY x, y", (selected_zoom,)
    ):
        with Image.open(io.BytesIO(data)) as tile_image:
            mosaic.alpha_composite(
                tile_image.convert("RGBA"),
                ((x - minimum_x) * tile_size, (y - minimum_y) * tile_size),
            )
    preview_path.parent.mkdir(parents=True, exist_ok=True)
    mosaic.save(preview_path, format="PNG", optimize=True)
    print(f"Preview z{selected_zoom}: {preview_path}", flush=True)


def write_preview(
    preview_path: Path,
    tiles: list[tuple[int, int, int, bytes]],
    tile_size: int,
) -> None:
    zooms = sorted({tile[0] for tile in tiles}, reverse=True)
    selected_zoom = zooms[-1]
    for zoom in zooms:
        zoom_tiles = [tile for tile in tiles if tile[0] == zoom]
        xs = [tile[1] for tile in zoom_tiles]
        ys = [tile[2] for tile in zoom_tiles]
        width = (max(xs) - min(xs) + 1) * tile_size
        height = (max(ys) - min(ys) + 1) * tile_size
        if width <= 4096 and height <= 4096:
            selected_zoom = zoom
            break
    zoom_tiles = [tile for tile in tiles if tile[0] == selected_zoom]
    minimum_x = min(tile[1] for tile in zoom_tiles)
    minimum_y = min(tile[2] for tile in zoom_tiles)
    maximum_x = max(tile[1] for tile in zoom_tiles)
    maximum_y = max(tile[2] for tile in zoom_tiles)
    mosaic = Image.new(
        "RGBA",
        ((maximum_x - minimum_x + 1) * tile_size, (maximum_y - minimum_y + 1) * tile_size),
    )
    for _, x, y, data in zoom_tiles:
        with Image.open(io.BytesIO(data)) as tile_image:
            mosaic.alpha_composite(
                tile_image.convert("RGBA"), ((x - minimum_x) * tile_size, (y - minimum_y) * tile_size)
            )
    preview_path.parent.mkdir(parents=True, exist_ok=True)
    mosaic.save(preview_path, format="PNG", optimize=True)
    print(f"Preview z{selected_zoom}: {preview_path}", flush=True)


def main() -> int:
    args = parse_arguments()
    if not args.input.is_file():
        raise FileNotFoundError(args.input)
    password = args.password.encode("ascii")
    print(f"Scanning {args.input} for shards intersecting {args.bounds} ...", flush=True)
    shards = discover_shards(args.input, password, args.bounds)
    if not shards:
        raise ValueError("no road-network shards intersect the requested bounds")
    record_count = sum(shard.record_count for shard in shards)
    print(
        f"Selected {len(shards)} shard(s), {record_count} records: "
        + ", ".join(shard.name for shard in shards),
        flush=True,
    )
    if record_count > args.max_records:
        raise ValueError(
            f"experiment selects {record_count} records, exceeding --max-records={args.max_records}; "
            "use smaller bounds or explicitly raise the guard"
        )

    tracks_by_id = load_tracks(args.input, shards, password)
    tracks = list(tracks_by_id.values())
    print(f"Loaded {len(tracks)} distinct ORIGINALID tracks.", flush=True)
    database_path = args.output.with_name(f"{args.output.name}.building.sqlite")
    signature = build_signature(
        args.input,
        shards,
        args.bounds,
        args.minzoom,
        args.maxzoom,
        args.tile_size,
        args.kernel_meters,
        args.metatile_size,
        args.tile_format,
    )
    connection = open_build_database(database_path, signature)
    completed = False
    try:
        p99_by_zoom, tile_count = generate_tiles_chunked(
            tracks,
            args.bounds,
            args.minzoom,
            args.maxzoom,
            args.tile_size,
            args.kernel_meters,
            args.metatile_size,
            args.tile_format,
            connection,
        )
        write_pmtiles_from_database(
            args.output,
            connection,
            args.bounds,
            args.minzoom,
            args.maxzoom,
            args.name,
            args.tile_size,
            [shard.name for shard in shards],
            len(tracks),
            p99_by_zoom,
            args.tile_format,
        )
        if args.preview:
            write_preview_from_database(args.preview, connection, args.tile_size)
        completed = True
    finally:
        connection.close()
    if completed:
        database_path.unlink(missing_ok=True)
    summary = {
        "output": str(args.output.resolve()),
        "bytes": args.output.stat().st_size,
        "bounds": args.bounds,
        "shards": [shard.name for shard in shards],
        "source_records": record_count,
        "distinct_tracks": len(tracks),
        "tiles": tile_count,
        "tile_format": args.tile_format,
        "p99_by_zoom": p99_by_zoom,
    }
    print(json.dumps(summary, indent=2), flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1) from error

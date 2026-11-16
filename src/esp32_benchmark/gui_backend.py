"""Reusable desktop-application helpers; importing this module does not require Tk."""

from __future__ import annotations

import json
import sqlite3
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Iterator

from .reporting import (
    write_comparison_csv,
    write_comparison_html,
    write_csv_report,
    write_html_report,
    write_json_report,
)
from .scoring import compare_results, make_reference_profile, score_result


ENVIRONMENTS = (
    "esp32dev",
    "esp32s2",
    "esp32s3",
    "esp32c3",
    "esp32c3_supermini",
    "esp32c6",
    "esp8266",
)


def list_serial_ports() -> list[dict[str, str]]:
    """Return available serial ports, including descriptions when pyserial is installed."""
    try:
        from serial.tools import list_ports
    except ImportError as error:
        raise RuntimeError(
            "Serial support is unavailable. Install it with `python -m pip install -e \".[hardware]\"`."
        ) from error
    return [
        {
            "port": item.device,
            "description": item.description or "Serial device",
            "hardware_id": item.hwid or "",
        }
        for item in list_ports.comports()
    ]


def available_environments(project_dir: str | Path) -> list[str]:
    """Read PlatformIO env names, falling back to the project's supported targets."""
    config_path = Path(project_dir) / "platformio.ini"
    if not config_path.exists():
        return list(ENVIRONMENTS)
    names = []
    for line in config_path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped.startswith("[env:") and stripped.endswith("]"):
            names.append(stripped[5:-1])
    return names or list(ENVIRONMENTS)


def recommend_environment(
    device: dict[str, Any] | None, port_description: str = ""
) -> tuple[str | None, str]:
    """Recommend a known PlatformIO target from firmware metadata, not USB guesses."""
    description = port_description.lower()
    if "super mini" in description or "supermini" in description:
        return "esp32c3_supermini", "Port description suggests an ESP32-C3 Super Mini."
    if not device:
        return None, "Identify the running benchmark firmware to get a board recommendation."

    family = str(device.get("family", "")).lower()
    target = str(device.get("target", "")).lower()
    architecture = str(device.get("architecture", "")).lower()
    identity = " ".join((family, target, architecture))
    if "esp8266" in identity or "lx106" in identity:
        return "esp8266", "Firmware identifies an ESP8266-family board."
    for soc, environment in (
        ("esp32-s2", "esp32s2"),
        ("esp32s3", "esp32s3"),
        ("esp32-s3", "esp32s3"),
        ("esp32c3", "esp32c3"),
        ("esp32-c3", "esp32c3"),
        ("esp32c6", "esp32c6"),
        ("esp32-c6", "esp32c6"),
    ):
        if soc in identity:
            return environment, f"Firmware identifies {soc.upper()}."
    if "esp32" in identity or "xtensa" in architecture:
        return "esp32dev", "Firmware identifies a classic ESP32-family target."
    return None, "Board identity was received, but no supported environment matched it."


def identify_board(port: str, baud: int = 115200, timeout: float = 6.0) -> dict[str, Any]:
    """Read a benchmark firmware run_start record; opening the port may reset the board."""
    try:
        import serial
    except ImportError as error:
        raise RuntimeError(
            "Board identification requires pyserial; install `python -m pip install -e \".[hardware]\"`."
        ) from error

    try:
        with serial.Serial(port, baudrate=baud, timeout=0.25) as connection:
            connection.reset_input_buffer()
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                raw = connection.readline()
                if not raw:
                    continue
                try:
                    record = json.loads(raw.decode("utf-8", errors="replace").strip())
                except json.JSONDecodeError:
                    continue
                if isinstance(record, dict) and record.get("type") == "run_start":
                    device = record.get("device")
                    if isinstance(device, dict):
                        return device
    except (OSError, serial.SerialException) as error:
        raise RuntimeError(f"Could not identify a board on {port}: {error}") from error
    raise TimeoutError(
        f"No ESP32xBenchmark firmware identity was received from {port}. "
        "Check the selected port and baud rate, then reset the board."
    )


def default_history_path() -> Path:
    """Use a per-user database, leaving the repository and benchmark artifacts untouched."""
    import os

    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    return base / "ESP32xBenchmark" / "history.sqlite3"


class HistoryStore:
    """Small SQLite store for captured results and measured reference profiles."""

    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as connection:
            connection.execute(
                """CREATE TABLE IF NOT EXISTS runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    target TEXT NOT NULL,
                    family TEXT NOT NULL,
                    comparability_class TEXT NOT NULL,
                    profile_id TEXT NOT NULL,
                    captured_at TEXT NOT NULL,
                    result_json TEXT NOT NULL
                )"""
            )
            connection.execute(
                """CREATE TABLE IF NOT EXISTS profiles (
                    profile_id TEXT PRIMARY KEY,
                    comparability_class TEXT NOT NULL,
                    profile_json TEXT NOT NULL,
                    source_path TEXT NOT NULL DEFAULT ''
                )"""
            )

    @contextmanager
    def _connect(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(self.path)
        connection.row_factory = sqlite3.Row
        try:
            with connection:
                yield connection
        finally:
            connection.close()

    def save_run(self, result: dict[str, Any], profile_id: str = "unscored") -> int:
        device = result.get("device", {})
        with self._connect() as connection:
            cursor = connection.execute(
                """INSERT INTO runs
                (target, family, comparability_class, profile_id, captured_at, result_json)
                VALUES (?, ?, ?, ?, ?, ?)""",
                (
                    str(device.get("target", "Unknown target")),
                    str(device.get("family", "unknown")),
                    str(device.get("comparability_class", "unknown")),
                    profile_id or "unscored",
                    str(result.get("captured_at", "")),
                    json.dumps(result, allow_nan=False),
                ),
            )
            return int(cursor.lastrowid)

    def list_runs(self) -> list[dict[str, Any]]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, target, family, comparability_class, profile_id, captured_at "
                "FROM runs ORDER BY id DESC"
            ).fetchall()
        return [dict(row) for row in rows]

    def get_run(self, run_id: int) -> dict[str, Any]:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT result_json FROM runs WHERE id = ?", (run_id,)
            ).fetchone()
        if row is None:
            raise KeyError(f"History entry {run_id} no longer exists.")
        return json.loads(row["result_json"])

    def save_profile(
        self,
        profile: dict[str, Any],
        source_path: str | Path = "",
    ) -> None:
        profile_id = str(profile.get("profile_id") or "")
        comparability_class = str(profile.get("comparability_class") or "")
        if not profile_id or not comparability_class:
            raise ValueError("Reference profile must include profile_id and comparability_class.")
        with self._connect() as connection:
            connection.execute(
                """INSERT INTO profiles (profile_id, comparability_class, profile_json, source_path)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(profile_id) DO UPDATE SET
                    comparability_class=excluded.comparability_class,
                    profile_json=excluded.profile_json,
                    source_path=excluded.source_path""",
                (
                    profile_id,
                    comparability_class,
                    json.dumps(profile, allow_nan=False),
                    str(source_path),
                ),
            )

    def list_profiles(self) -> list[dict[str, str]]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT profile_id, comparability_class, source_path FROM profiles "
                "ORDER BY profile_id"
            ).fetchall()
        return [dict(row) for row in rows]

    def get_profile(self, profile_id: str) -> dict[str, Any]:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT profile_json FROM profiles WHERE profile_id = ?", (profile_id,)
            ).fetchone()
        if row is None:
            raise KeyError(f"Reference profile {profile_id!r} is not in the profile library.")
        return json.loads(row["profile_json"])

    def delete_run(self, run_id: int) -> None:
        with self._connect() as connection:
            connection.execute("DELETE FROM runs WHERE id = ?", (run_id,))


def load_profile(path: str | Path) -> dict[str, Any]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or not isinstance(payload.get("metrics"), dict):
        raise ValueError("Reference profile file must contain a profile object with metrics.")
    if not payload.get("profile_id") or not payload.get("comparability_class"):
        raise ValueError("Reference profile is missing profile_id or comparability_class.")
    return payload


def load_result(path: str | Path) -> dict[str, Any]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or not isinstance(payload.get("metrics"), list):
        raise ValueError("Result file must contain a benchmark object with a metrics list.")
    if not isinstance(payload.get("device"), dict):
        raise ValueError("Result file is missing device metadata.")
    return payload


def score_and_write_reports(
    result: dict[str, Any],
    profile: dict[str, Any] | None,
    output_dir: str | Path,
) -> dict[str, Any]:
    """Score using the project's scorer and write its standard JSON/CSV/HTML reports."""
    if profile is not None:
        result["scores"] = score_result(result, profile)
    output = Path(output_dir)
    write_json_report(result, output / "results.json")
    write_csv_report(result, output / "results.csv")
    write_html_report(result, output / "results.html")
    return result


def compare_and_write_reports(
    results: list[dict[str, Any]],
    profile: dict[str, Any],
    output_dir: str | Path,
) -> dict[str, Any]:
    """Use the shared stable-metric comparison API and standard report writers."""
    comparison = compare_results(results, profile)
    output = Path(output_dir)
    write_json_report(comparison, output / "comparison.json")
    write_comparison_csv(comparison, output / "comparison.csv")
    write_comparison_html(comparison, output / "comparison.html")
    return comparison


def calibrate_result(
    result: dict[str, Any], profile_id: str
) -> dict[str, Any]:
    return make_reference_profile(result, profile_id)

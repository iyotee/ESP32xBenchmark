from __future__ import annotations

import argparse
import json
import platform
import subprocess
import sys
from pathlib import Path
from typing import Any

from .collector import collect_serial_runs
from .reporting import write_csv_report, write_html_report, write_json_report
from .scoring import make_reference_profile, score_result


def _read_json(path: str | Path) -> dict[str, Any]:
    with Path(path).open(encoding="utf-8") as file:
        payload = json.load(file)
    if not isinstance(payload, dict):
        raise ValueError(f"{path} must contain a JSON object.")
    return payload


def _write_reports(result: dict[str, Any], output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    write_json_report(result, output_dir / "results.json")
    write_csv_report(result, output_dir / "results.csv")
    write_html_report(result, output_dir / "results.html")


def _merge_supplement(result: dict[str, Any], path: Path | None) -> None:
    if path is None:
        return
    supplement = _read_json(path)
    metrics = supplement.get("metrics")
    if not isinstance(metrics, list) or not all(isinstance(item, dict) for item in metrics):
        raise ValueError("Supplement file must contain a 'metrics' list of objects.")
    result["metrics"].extend(metrics)


def _capture(args: argparse.Namespace) -> dict[str, Any]:
    if args.upload:
        command = ["pio", "run", "-t", "upload"]
        if args.environment:
            command.extend(["-e", args.environment])
        subprocess.run(command, cwd=args.project_dir, check=True)
    result = collect_serial_runs(args.port, args.baud, args.timeout, args.runs)
    result["host"] = {
        "collector_version": "0.2.0",
        "python_version": platform.python_version(),
        "platform": platform.platform(),
        "platformio_environment": args.environment or "esp32dev",
        "serial_baud": args.baud,
    }
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description="ESP32xBenchmark serial capture and scoring tools.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    capture = subparsers.add_parser("capture", help="Capture one completed firmware run.")
    capture.add_argument("--port", required=True, help="Serial port, e.g. COM6 or /dev/ttyUSB0.")
    capture.add_argument("--baud", type=int, default=115200)
    capture.add_argument("--timeout", type=int, default=180)
    capture.add_argument("--runs", type=int, default=3, help="Number of independent firmware runs to aggregate.")
    capture.add_argument("--project-dir", type=Path, default=Path("platformio_test"))
    capture.add_argument("--environment", help="PlatformIO environment name.")
    capture.add_argument("--upload", action="store_true", help="Build and flash before capture.")
    capture.add_argument("--output-dir", type=Path, default=Path("artifacts"))
    capture.add_argument("--reference", type=Path, help="Reference profile used to calculate scores.")
    capture.add_argument("--calibrate", metavar="PROFILE_ID", help="Save this run as a 100-point reference.")
    capture.add_argument("--profile-output", type=Path, help="Path for the generated calibration profile.")
    capture.add_argument("--supplement", type=Path, help="JSON file with peer/instrument metric records.")

    score = subparsers.add_parser("score", help="Score an existing captured results.json.")
    score.add_argument("result", type=Path)
    score.add_argument("--reference", type=Path, required=True)
    score.add_argument("--output-dir", type=Path, default=Path("artifacts"))
    score.add_argument("--supplement", type=Path, help="JSON file with peer/instrument metric records.")

    args = parser.parse_args()
    try:
        if args.command == "capture":
            result = _capture(args)
            _merge_supplement(result, args.supplement)
            if args.calibrate:
                profile = make_reference_profile(result, args.calibrate)
                profile_path = args.profile_output or (args.output_dir / "reference_profile.json")
                profile_path.parent.mkdir(parents=True, exist_ok=True)
                profile_path.write_text(
                    json.dumps(profile, indent=2, allow_nan=False) + "\n", encoding="utf-8"
                )
                result["scores"] = score_result(result, profile)
                print(f"Calibration profile written to {profile_path}")
            elif args.reference:
                result["scores"] = score_result(result, _read_json(args.reference))
            _write_reports(result, args.output_dir)
            print(f"Reports written to {args.output_dir.resolve()}")
            print(f"Measurements captured: {len(result['metrics'])}")
            if "scores" in result:
                print(json.dumps(result["scores"], indent=2))
        elif args.command == "score":
            result = _read_json(args.result)
            _merge_supplement(result, args.supplement)
            result["scores"] = score_result(result, _read_json(args.reference))
            _write_reports(result, args.output_dir)
            print(f"Global benchmark index: {result['scores']['global_score']}")
            print(f"Coverage: {result['scores']['coverage']:.0%}")
            print(f"Scored report written to {args.output_dir.resolve()}")
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.CalledProcessError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1) from error


if __name__ == "__main__":
    main()

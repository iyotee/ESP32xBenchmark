from __future__ import annotations

import json
import statistics
import time
from typing import Any


def aggregate_runs(runs: list[dict[str, Any]]) -> dict[str, Any]:
    if not runs:
        raise ValueError("At least one complete run is required.")
    first_device = runs[0]["device"]
    for run in runs[1:]:
        device = run["device"]
        if (
            device.get("comparability_class") != first_device.get("comparability_class")
            or device.get("target") != first_device.get("target")
        ):
            raise ValueError("Repeated captures must come from the same target and class.")

    metrics_by_name: dict[str, list[dict[str, Any]]] = {}
    for run in runs:
        for metric in run["metrics"]:
            name = metric.get("name")
            if isinstance(name, str):
                metrics_by_name.setdefault(name, []).append(metric)

    aggregated_metrics: list[dict[str, Any]] = []
    for name, measurements in metrics_by_name.items():
        successful = [
            metric
            for metric in measurements
            if metric.get("status") == "ok"
            and isinstance(metric.get("value"), (int, float))
        ]
        if not successful:
            aggregated_metrics.append(measurements[0])
            continue
        if len(successful) != len(runs):
            aggregated_metrics.append(
                {
                    **successful[0],
                    "value": None,
                    "status": "partial",
                    "reason": "metric was not measured successfully in every repeated run",
                    "successful_runs": len(successful),
                    "total_runs": len(runs),
                }
            )
            continue

        values = [float(metric["value"]) for metric in successful]
        center = statistics.median(values)
        mean = statistics.fmean(values)
        between_run_stdev = (
            statistics.pstdev(values) * 100.0 / abs(mean) if mean else 0.0
        )
        within_run_values = [
            float(metric.get("stdev_pct", 0.0))
            for metric in successful
            if isinstance(metric.get("stdev_pct", 0.0), (int, float))
        ]
        within_run_stdev = max(within_run_values, default=0.0)
        aggregated_metrics.append(
            {
                **successful[0],
                "value": center,
                "median": center,
                "min": min(values),
                "max": max(values),
                "stdev_pct": max(between_run_stdev, within_run_stdev),
                "within_run_stdev_pct": within_run_stdev,
                "between_run_stdev_pct": between_run_stdev,
                "samples": sum(int(metric.get("samples", 1)) for metric in successful),
            }
        )

    return {
        "schema_version": 1,
        "run_id": runs[0].get("run_id", ""),
        "run_count": len(runs),
        "device": first_device,
        "metrics": aggregated_metrics,
        "run_summaries": [
            {
                "run_id": run.get("run_id", ""),
                "captured_at": run.get("captured_at"),
                "metrics": [
                    {"name": metric.get("name"), "value": metric.get("value")}
                    for metric in run["metrics"]
                ],
            }
            for run in runs
        ],
        "captured_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }


def collect_serial_runs(
    port: str, baud: int = 115200, timeout: int = 180, run_count: int = 3
) -> dict[str, Any]:
    try:
        import serial
    except ImportError as error:
        raise RuntimeError(
            "Serial collection requires pyserial; install with `pip install -e .[hardware]`."
        ) from error
    if run_count < 1:
        raise ValueError("run_count must be at least one.")

    deadline = time.monotonic() + timeout
    runs: list[dict[str, Any]] = []
    device: dict[str, Any] | None = None
    metrics: list[dict[str, Any]] = []
    run_id = ""

    with serial.Serial(port, baudrate=baud, timeout=0.5) as connection:
        while time.monotonic() < deadline:
            raw_line = connection.readline()
            if not raw_line:
                continue
            try:
                record = json.loads(raw_line.decode("utf-8", errors="replace").strip())
            except json.JSONDecodeError:
                continue
            if not isinstance(record, dict):
                continue
            record_type = record.get("type")
            if record_type == "run_start":
                device = record.get("device")
                if not isinstance(device, dict):
                    raise ValueError("Serial run_start record is missing device metadata.")
                metrics = []
                run_id = str(record.get("run_id", ""))
            elif record_type == "metric" and device is not None:
                metrics.append(record)
            elif record_type == "run_complete" and device is not None:
                runs.append(
                    {
                        "run_id": run_id,
                        "device": device,
                        "metrics": metrics,
                        "captured_at": time.strftime(
                            "%Y-%m-%dT%H:%M:%SZ", time.gmtime()
                        ),
                    }
                )
                device = None
                metrics = []
                if len(runs) >= run_count:
                    break

    if len(runs) != run_count:
        raise TimeoutError(
            f"Received {len(runs)} of {run_count} complete benchmark runs from "
            f"{port} within {timeout} seconds."
        )
    return aggregate_runs(runs)

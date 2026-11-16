from __future__ import annotations

from collections import defaultdict
from math import isfinite
from typing import Any


CATEGORY_WEIGHTS = {
    "cpu": 0.20,
    "memory": 0.18,
    "connectivity": 0.20,
    "power": 0.18,
    "peripherals": 0.10,
    "rtos": 0.10,
    "ai": 0.04,
}
MAX_STDEV_PERCENT = 25.0


def _positive_number(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{label} must be a number.")
    number = float(value)
    if not isfinite(number) or number <= 0:
        raise ValueError(f"{label} must be a finite number greater than zero.")
    return number


def score_result(
    result: dict[str, Any], reference_profile: dict[str, Any]
) -> dict[str, Any]:
    """Score only measured metrics with matching, same-class calibration data."""
    device = result.get("device")
    if not isinstance(device, dict):
        raise ValueError("Result is missing device metadata.")
    result_class = device.get("comparability_class")
    reference_class = reference_profile.get("comparability_class")
    if not result_class or result_class != reference_class:
        raise ValueError(
            "Result and reference profile must have the same comparability_class."
        )

    reference_metrics = reference_profile.get("metrics")
    measured_metrics = result.get("metrics")
    if not isinstance(reference_metrics, dict) or not isinstance(measured_metrics, list):
        raise ValueError("Invalid reference profile or result metric format.")

    category_values: dict[str, list[dict[str, Any]]] = defaultdict(list)
    unstable_metrics: list[str] = []
    unmatched_metrics: list[str] = []
    for metric in measured_metrics:
        if not isinstance(metric, dict):
            raise ValueError("Each measurement must be an object.")
        if metric.get("status") != "ok":
            continue
        category = metric.get("category")
        name = metric.get("name")
        if category not in CATEGORY_WEIGHTS or not isinstance(name, str):
            raise ValueError("Scored measurements need a known category and metric name.")
        reference = reference_metrics.get(name)
        if not isinstance(reference, dict):
            unmatched_metrics.append(name)
            continue
        if reference.get("category") != category:
            unmatched_metrics.append(name)
            continue
        if reference.get("direction") != metric.get("direction"):
            unmatched_metrics.append(name)
            continue
        if metric.get("direction") not in {"higher", "lower"}:
            raise ValueError(f"{name}.direction must be 'higher' or 'lower'.")
        stdev_percent = metric.get("stdev_pct")
        if stdev_percent is not None:
            if isinstance(stdev_percent, bool) or not isinstance(stdev_percent, (int, float)):
                raise ValueError(f"{name}.stdev_pct must be a non-negative number.")
            stdev_percent = float(stdev_percent)
            if not isfinite(stdev_percent) or stdev_percent < 0:
                raise ValueError(f"{name}.stdev_pct must be a finite non-negative number.")
            if stdev_percent > MAX_STDEV_PERCENT:
                unstable_metrics.append(name)
                continue
        observed = _positive_number(metric.get("value"), f"{name}.value")
        baseline = _positive_number(reference.get("value"), f"{name}.reference")
        if not isfinite(observed / baseline):
            continue
        index = observed / baseline if metric["direction"] == "higher" else baseline / observed
        if not isfinite(index):
            continue
        category_values[category].append(
            {
                "name": name,
                "value": round(index * 100.0, 2),
                "observed": observed,
                "reference": baseline,
                "unit": metric.get("unit", reference.get("unit", "")),
            }
        )

    category_scores: dict[str, dict[str, Any]] = {}
    available_weight = 0.0
    weighted_sum = 0.0
    for category, values in category_values.items():
        if not values:
            continue
        category_score = sum(item["value"] for item in values) / len(values)
        category_scores[category] = {
            "score": round(category_score, 2),
            "weight": CATEGORY_WEIGHTS[category],
            "metrics": values,
        }
        available_weight += CATEGORY_WEIGHTS[category]
        weighted_sum += category_score * CATEGORY_WEIGHTS[category]

    total_weight = sum(CATEGORY_WEIGHTS.values())
    return {
        "reference_profile": reference_profile.get("profile_id", "unnamed"),
        "score_model": "weighted-category-index-v1",
        "global_score": round(weighted_sum / available_weight, 2) if available_weight else None,
        "score_scale": "reference=100; values are not capped",
        "coverage": round(available_weight / total_weight, 4),
        "available_weight": round(available_weight, 4),
        "categories": category_scores,
        "excluded_categories": [
            category for category in CATEGORY_WEIGHTS if category not in category_scores
        ],
        "unstable_metrics": unstable_metrics,
        "unmatched_metrics": unmatched_metrics,
        "interpretation": (
            "Power requires an external current monitor; AI/TinyML requires a supported "
            "model/runtime. Missing categories are excluded and coverage is reported."
        ),
    }


def make_reference_profile(result: dict[str, Any], profile_id: str) -> dict[str, Any]:
    """Capture a measured run as the 100-point calibration reference."""
    device = result.get("device")
    if not isinstance(device, dict) or not device.get("comparability_class"):
        raise ValueError("Result has no comparability_class metadata.")
    metrics: dict[str, dict[str, Any]] = {}
    for metric in result.get("metrics", []):
        if (
            not isinstance(metric, dict)
            or metric.get("status") != "ok"
            or metric.get("category") not in CATEGORY_WEIGHTS
        ):
            continue
        stdev_percent = metric.get("stdev_pct")
        if isinstance(stdev_percent, (int, float)) and stdev_percent > MAX_STDEV_PERCENT:
            continue
        try:
            value = _positive_number(metric.get("value"), "metric.value")
        except ValueError:
            continue
        name = metric.get("name")
        direction = metric.get("direction")
        if not isinstance(name, str) or direction not in {"higher", "lower"}:
            continue
        metrics[name] = {
            "category": metric["category"],
            "value": value,
            "unit": metric.get("unit", ""),
            "direction": direction,
        }
    if not metrics:
        raise ValueError("No valid measured metrics are available for calibration.")
    return {
        "schema_version": 1,
        "profile_id": profile_id,
        "reference_run_id": result.get("run_id"),
        "reference_run_ids": [
            run.get("run_id") for run in result.get("run_summaries", [])
        ],
        "calibration_run_count": result.get("run_count", 1),
        "calibrated_at": result.get("captured_at"),
        "comparability_class": device["comparability_class"],
        "reference_device": device,
        "reference_host": result.get("host"),
        "metrics": metrics,
        "notice": "Measured calibration run; reference values are not manufacturer specifications.",
    }

import json

import pytest

from esp32_benchmark.collector import aggregate_runs
from esp32_benchmark.reporting import write_csv_report, write_html_report, write_json_report
from esp32_benchmark.scoring import make_reference_profile, score_result


@pytest.fixture
def result():
    return {
        "schema_version": 1,
        "run_id": "test-run",
        "device": {
            "target": "ESP32 test",
            "family": "ESP32",
            "comparability_class": "classic_wifi_ble",
        },
        "metrics": [
            {
                "category": "cpu",
                "name": "integer_mix_miter_s",
                "value": 20.0,
                "unit": "Mops/s",
                "direction": "higher",
                "status": "ok",
            },
            {
                "category": "rtos",
                "name": "task_handoff_us",
                "value": 12.0,
                "unit": "us",
                "direction": "lower",
                "status": "ok",
            },
            {
                "category": "connectivity",
                "name": "wifi_scan_ms",
                "value": 2500.0,
                "unit": "ms",
                "direction": "lower",
                "status": "informational",
            },
            {
                "category": "power",
                "name": "active_current_ma",
                "value": None,
                "unit": "",
                "direction": "higher",
                "status": "skipped",
            },
        ],
    }


def test_calibrated_reference_scores_100_and_excludes_unavailable_categories(result):
    profile = make_reference_profile(result, "esp32-baseline")
    scores = score_result(result, profile)

    assert scores["global_score"] == 100.0
    assert scores["coverage"] == pytest.approx(0.30)
    assert scores["categories"]["cpu"]["score"] == 100.0
    assert scores["categories"]["rtos"]["score"] == 100.0
    assert "connectivity" in scores["excluded_categories"]
    assert "power" in scores["excluded_categories"]


def test_score_obeys_metric_direction_and_rejects_cross_class_comparisons(result):
    profile = make_reference_profile(result, "baseline")
    result["metrics"][0]["value"] = 30.0
    result["metrics"][1]["value"] = 6.0

    scores = score_result(result, profile)
    assert scores["categories"]["cpu"]["score"] == 150.0
    assert scores["categories"]["rtos"]["score"] == 200.0

    profile["comparability_class"] = "wifi6_802154"
    with pytest.raises(ValueError, match="same comparability_class"):
        score_result(result, profile)


def test_external_power_measurement_contributes_to_global_score(result):
    profile = make_reference_profile(result, "baseline")
    profile["metrics"]["active_current_ma"] = {
        "category": "power",
        "value": 40.0,
        "unit": "mA",
        "direction": "lower",
    }
    result["metrics"].append(
        {
            "category": "power",
            "name": "active_current_ma",
            "value": 50.0,
            "unit": "mA",
            "direction": "lower",
            "status": "ok",
        }
    )

    scores = score_result(result, profile)
    assert scores["categories"]["power"]["score"] == 80.0
    assert scores["coverage"] == pytest.approx(0.48)
    assert scores["global_score"] == pytest.approx(92.5)


def test_unstable_measurement_is_excluded_from_scores_and_calibration(result):
    profile = make_reference_profile(result, "baseline")
    result["metrics"][0]["stdev_pct"] = 30.0
    profile["metrics"]["integer_mix_miter_s"]["stdev_pct"] = 30.0

    scores = score_result(result, profile)
    assert "integer_mix_miter_s" in scores["unstable_metrics"]
    assert "cpu" not in scores["categories"]
    assert scores["global_score"] == 100.0

    calibrated = make_reference_profile(result, "baseline-with-stability-filter")
    assert "integer_mix_miter_s" not in calibrated["metrics"]


def test_repeated_runs_aggregate_medians_and_detect_between_run_variation(result):
    template = result["metrics"][0]
    runs = [
        {
            "run_id": f"run-{value}",
            "device": result["device"],
            "metrics": [{**template, "value": value, "stdev_pct": 1.0}],
        }
        for value in (10.0, 20.0, 30.0)
    ]

    aggregated = aggregate_runs(runs)
    metric = aggregated["metrics"][0]
    assert aggregated["run_count"] == 3
    assert metric["value"] == 20.0
    assert metric["min"] == 10.0
    assert metric["max"] == 30.0
    assert metric["between_run_stdev_pct"] == pytest.approx(40.82, abs=0.01)
    assert metric["stdev_pct"] == pytest.approx(40.82, abs=0.01)


def test_report_writers_emit_machine_and_human_readable_outputs(tmp_path, result):
    profile = make_reference_profile(result, "baseline")
    result["scores"] = score_result(result, profile)

    json_path = write_json_report(result, tmp_path / "nested" / "results.json")
    csv_path = write_csv_report(result, tmp_path / "results.csv")
    html_path = write_html_report(result, tmp_path / "results.html")

    assert json.loads(json_path.read_text(encoding="utf-8"))["scores"]["global_score"] == 100.0
    assert "global_score" in csv_path.read_text(encoding="utf-8")
    assert "100.00" in html_path.read_text(encoding="utf-8")


def test_reference_profile_requires_measured_values(result):
    result["metrics"] = [
        {"category": "power", "name": "current", "value": None, "status": "skipped"}
    ]
    with pytest.raises(ValueError, match="No valid measured metrics"):
        make_reference_profile(result, "empty")

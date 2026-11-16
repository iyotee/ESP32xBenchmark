import json

from esp32_benchmark.gui import BenchmarkApp
from esp32_benchmark.gui_backend import (
    HistoryStore,
    available_environments,
    compare_and_write_reports,
    recommend_environment,
)
from esp32_benchmark.scoring import make_reference_profile


def _result(target="ESP32 test", family="esp32", value=20.0):
    return {
        "schema_version": 1,
        "run_id": target.replace(" ", "-"),
        "captured_at": "2026-01-02T03:04:05Z",
        "device": {
            "target": target,
            "family": family,
            "comparability_class": "classic_wifi_ble" if family == "esp32" else "esp8266_wifi",
        },
        "metrics": [
            {
                "category": "cpu",
                "name": "integer_mix_miter_s",
                "value": value,
                "unit": "Mops/s",
                "direction": "higher",
                "status": "ok",
                "stdev_pct": 1.0,
            }
        ],
    }


def test_environment_recommendation_uses_firmware_identity_and_not_generic_usb_ids():
    assert recommend_environment({"family": "esp8266", "target": "ESP8266EX"})[0] == "esp8266"
    assert recommend_environment({"target": "ESP32-C3", "family": "esp32"})[0] == "esp32c3"
    assert recommend_environment(None, "USB Serial Device")[0] is None
    assert recommend_environment(None, "ESP32-C3 Super Mini")[0] == "esp32c3_supermini"


def test_platformio_environment_discovery_reads_environment_sections(tmp_path):
    (tmp_path / "platformio.ini").write_text(
        "[platformio]\ndefault_envs = a\n[env:a]\nboard = test\n[env:b]\nboard = test\n",
        encoding="utf-8",
    )
    assert available_environments(tmp_path) == ["a", "b"]


def test_upload_command_targets_the_selected_com_port(monkeypatch):
    monkeypatch.setattr("esp32_benchmark.gui.shutil.which", lambda _name: "pio")

    command = BenchmarkApp._build_command(object(), True, "esp32dev", "COM7")

    assert command == [
        "pio", "run", "-e", "esp32dev", "-t", "upload", "--upload-port", "COM7"
    ]


def test_history_persists_runs_profiles_and_restores_result(tmp_path):
    store = HistoryStore(tmp_path / "history.sqlite3")
    result = _result()
    profile = make_reference_profile(result, "baseline")
    store.save_profile(profile, "reference.json")
    run_id = store.save_run(result, "baseline")

    assert store.list_profiles() == [{
        "profile_id": "baseline",
        "comparability_class": "classic_wifi_ble",
        "source_path": "reference.json",
    }]
    assert store.get_run(run_id)["device"]["target"] == "ESP32 test"
    assert store.list_runs()[0]["profile_id"] == "baseline"


def test_comparison_uses_shared_scoring_api_and_writes_standard_reports(tmp_path):
    reference = make_reference_profile(_result(), "shared-reference")
    results = [_result("ESP32 test", "esp32", 20.0), _result("ESP8266EX", "esp8266", 10.0)]

    comparison = compare_and_write_reports(results, reference, tmp_path / "reports")

    assert [item["global_score"] for item in comparison["results"]] == [100.0, 50.0]
    assert comparison["common_metrics"] == ["integer_mix_miter_s"]
    assert json.loads((tmp_path / "reports" / "comparison.json").read_text(encoding="utf-8"))[
        "reference_profile"
    ] == "shared-reference"
    assert "Common index" in (tmp_path / "reports" / "comparison.html").read_text(encoding="utf-8")

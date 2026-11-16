from __future__ import annotations

import csv
import html
import json
from pathlib import Path
from typing import Any


def write_json_report(result: dict[str, Any], output_path: str | Path) -> Path:
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return output


def write_csv_report(result: dict[str, Any], output_path: str | Path) -> Path:
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    scores = result.get("scores", {})
    category_scores = scores.get("categories", {})
    with output.open("w", newline="", encoding="utf-8") as file:
        writer = csv.writer(file)
        writer.writerow(
            [
                "target",
                "family",
                "comparability_class",
                "category",
                "metric",
                "value",
                "min",
                "max",
                "stdev_pct",
                "unit",
                "direction",
                "status",
                "category_score",
                "global_score",
                "coverage",
            ]
        )
        for metric in result.get("metrics", []):
            category = metric.get("category", "")
            category_score = category_scores.get(category, {}).get("score", "")
            writer.writerow(
                [
                    result.get("device", {}).get("target", ""),
                    result.get("device", {}).get("family", ""),
                    result.get("device", {}).get("comparability_class", ""),
                    category,
                    metric.get("name", ""),
                    metric.get("value", ""),
                    metric.get("min", ""),
                    metric.get("max", ""),
                    metric.get("stdev_pct", ""),
                    metric.get("unit", ""),
                    metric.get("direction", ""),
                    metric.get("status", ""),
                    category_score,
                    scores.get("global_score", ""),
                    scores.get("coverage", ""),
                ]
            )
    return output


def write_html_report(result: dict[str, Any], output_path: str | Path) -> Path:
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    device = result.get("device", {})
    scores = result.get("scores", {})
    categories = scores.get("categories", {})

    category_rows = "".join(
        "<tr><td>{}</td><td>{:.2f}</td><td>{:.0%}</td></tr>".format(
            html.escape(name), info["score"], info["weight"]
        )
        for name, info in categories.items()
    )
    metric_rows = "".join(
        "<tr><td>{}</td><td>{}</td><td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>".format(
            html.escape(str(metric.get("category", ""))),
            html.escape(str(metric.get("name", ""))),
            html.escape(str(metric.get("value", "n/a"))),
            html.escape(
                "{} / {} / {}%".format(
                    metric.get("min", "n/a"),
                    metric.get("max", "n/a"),
                    metric.get("stdev_pct", "n/a"),
                )
            ),
            html.escape(str(metric.get("unit", ""))),
            html.escape(str(metric.get("status", "unknown"))),
        )
        for metric in result.get("metrics", [])
    )
    global_score = scores.get("global_score")
    score_text = "N/A" if global_score is None else f"{global_score:.2f}"
    excluded = ", ".join(scores.get("excluded_categories", [])) or "None"
    unstable = ", ".join(scores.get("unstable_metrics", [])) or "None"
    unmatched = ", ".join(scores.get("unmatched_metrics", [])) or "None"
    html_content = f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ESP32xBenchmark results</title>
<style>
body{{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;background:#0b1220;color:#e5edf8}}
article{{background:#121d30;border:1px solid #293953;border-radius:12px;padding:1rem;margin:1rem 0}}
h1,h2{{color:#8ed8ff}} .score{{font-size:3rem;font-weight:700}} table{{border-collapse:collapse;width:100%}}
th,td{{padding:.65rem;text-align:left;border-bottom:1px solid #293953}} th{{color:#8ed8ff}}
small,.muted{{color:#adbbcf}}
</style>
</head>
<body>
<h1>ESP32xBenchmark</h1>
<article><h2>{html.escape(str(device.get("target", "Unknown target")))}</h2>
<p>Family: {html.escape(str(device.get("family", "unknown")))} ·
Class: {html.escape(str(device.get("comparability_class", "unknown")))} ·
Run: {html.escape(str(result.get("run_id", "unknown")))}</p>
<div class="score">{score_text}<small> reference index</small></div>
<p>Coverage: {scores.get("coverage", 0):.0%} of configured category weights.
Reference: {html.escape(str(scores.get("reference_profile", "not scored")))}.
Categories excluded: {html.escape(excluded)}.</p>
<p class="muted">Reference run = 100. Scores are not capped. Scores are comparable only within the same calibration class and profile.</p>
</article>
<article><h2>Category scores</h2><table><thead><tr><th>Category</th><th>Index</th><th>Weight</th></tr></thead><tbody>{category_rows}</tbody></table></article>
<article><h2>Raw measurements</h2><table><thead><tr><th>Category</th><th>Metric</th><th>Median</th><th>Min / max / stdev %</th><th>Unit</th><th>Status</th></tr></thead><tbody>{metric_rows}</tbody></table></article>
<article><h2>Scoring exclusions</h2><p>Unstable metrics (&gt;25% variation): {html.escape(unstable)}</p><p>Metrics without a matching reference: {html.escape(unmatched)}</p></article>
<p class="muted">Unmeasured categories are not fabricated. Power needs an external current monitor; AI/TinyML needs a supported model/runtime.</p>
</body>
</html>
"""
    output.write_text(html_content, encoding="utf-8")
    return output


def write_comparison_csv(
    comparison: dict[str, Any], output_path: str | Path
) -> Path:
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    categories = comparison.get("common_categories", [])
    with output.open("w", newline="", encoding="utf-8") as file:
        writer = csv.writer(file)
        writer.writerow(
            [
                "target",
                "family",
                "comparability_class",
                "global_score",
                "coverage",
                *categories,
            ]
        )
        for result in comparison.get("results", []):
            category_scores = result.get("categories", {})
            writer.writerow(
                [
                    result.get("target", ""),
                    result.get("family", ""),
                    result.get("comparability_class", ""),
                    result.get("global_score", ""),
                    result.get("coverage", ""),
                    *[
                        category_scores.get(category, {}).get("score", "")
                        for category in categories
                    ],
                ]
            )
    return output


def write_comparison_html(
    comparison: dict[str, Any], output_path: str | Path
) -> Path:
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    categories = comparison.get("common_categories", [])
    header = "".join(f"<th>{html.escape(category)}</th>" for category in categories)
    rows = []
    for result in comparison.get("results", []):
        category_scores = result.get("categories", {})
        category_cells = "".join(
            "<td>{:.2f}</td>".format(category_scores[category]["score"])
            for category in categories
        )
        rows.append(
            "<tr><td>{}</td><td>{}</td><td>{}</td><td>{:.2f}</td>"
            "<td>{:.0%}</td>{}</tr>".format(
                html.escape(str(result.get("target", "unknown"))),
                html.escape(str(result.get("family", "unknown"))),
                html.escape(str(result.get("comparability_class", "unknown"))),
                result.get("global_score", 0.0),
                result.get("coverage", 0.0),
                category_cells,
            )
        )
    metric_list = ", ".join(
        html.escape(name) for name in comparison.get("common_metrics", [])
    )
    content = f"""<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>ESP32xBenchmark comparison</title>
<style>
body{{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;background:#0b1220;color:#e5edf8}}
article{{background:#121d30;border:1px solid #293953;border-radius:12px;padding:1rem;margin:1rem 0}}
h1,h2,th{{color:#8ed8ff}} table{{border-collapse:collapse;width:100%}}
th,td{{padding:.65rem;text-align:left;border-bottom:1px solid #293953}}
.score{{font-size:1.15rem;font-weight:700}} .muted{{color:#adbbcf}}
</style></head>
<body><h1>ESP32xBenchmark cross-board comparison</h1>
<article><p>Reference: <b>{html.escape(str(comparison.get("reference_profile", "unknown")))}</b>
({html.escape(str(comparison.get("reference_class", "unknown")))}).
Common coverage: {comparison.get("coverage", 0):.0%}.</p>
<p>Common measured metrics: {metric_list}</p>
<p class="muted">{html.escape(str(comparison.get("interpretation", "")))}</p></article>
<article><table><thead><tr><th>Target</th><th>Family</th><th>Class</th>
<th>Common index</th><th>Coverage</th>{header}</tr></thead>
<tbody>{"".join(rows)}</tbody></table></article>
</body></html>
"""
    output.write_text(content, encoding="utf-8")
    return output

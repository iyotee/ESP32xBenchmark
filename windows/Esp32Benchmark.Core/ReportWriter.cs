using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esp32Benchmark.Core;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void WriteResult(JsonObject result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteJson(result, Path.Combine(outputDirectory, "results.json"));
        WriteResultCsv(result, Path.Combine(outputDirectory, "results.csv"));
        WriteResultHtml(result, Path.Combine(outputDirectory, "results.html"));
    }

    public static void WriteComparison(JsonObject comparison, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteJson(comparison, Path.Combine(outputDirectory, "comparison.json"));
        WriteComparisonCsv(comparison, Path.Combine(outputDirectory, "comparison.csv"));
        WriteComparisonHtml(comparison, Path.Combine(outputDirectory, "comparison.html"));
    }

    public static void WriteJson(JsonNode payload, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, payload.ToJsonString(JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteResultCsv(JsonObject result, string path)
    {
        var device = result["device"] as JsonObject ?? new JsonObject();
        var scores = result["scores"] as JsonObject;
        var categories = scores?["categories"] as JsonObject;
        var builder = new StringBuilder();
        AppendCsvRow(builder, "target", "family", "comparability_class", "category", "metric",
            "value", "min", "max", "stdev_pct", "unit", "direction", "status",
            "category_score", "global_score", "coverage");

        if (result["metrics"] is JsonArray metrics)
        {
            foreach (var metric in metrics.OfType<JsonObject>())
            {
                var category = BenchmarkEngine.String(metric["category"]);
                var categoryScore = categories?[category]?["score"];
                AppendCsvRow(builder,
                    BenchmarkEngine.String(device["target"]),
                    BenchmarkEngine.String(device["family"]),
                    BenchmarkEngine.String(device["comparability_class"]),
                    category,
                    BenchmarkEngine.String(metric["name"]),
                    CsvValue(metric["value"]),
                    CsvValue(metric["min"]),
                    CsvValue(metric["max"]),
                    CsvValue(metric["stdev_pct"]),
                    BenchmarkEngine.String(metric["unit"]),
                    BenchmarkEngine.String(metric["direction"]),
                    BenchmarkEngine.String(metric["status"]),
                    CsvValue(categoryScore),
                    CsvValue(scores?["global_score"]),
                    CsvValue(scores?["coverage"]));
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteResultHtml(JsonObject result, string path)
    {
        var device = result["device"] as JsonObject ?? new JsonObject();
        var scores = result["scores"] as JsonObject;
        var categories = scores?["categories"] as JsonObject;
        var categoryRows = new StringBuilder();
        if (categories is not null)
        {
            foreach (var (category, node) in categories)
            {
                var item = node as JsonObject;
                categoryRows.Append("<tr><td>").Append(H(category)).Append("</td><td>")
                    .Append(H(CsvValue(item?["score"]))).Append("</td><td>")
                    .Append(FormatPercent(item?["weight"])).Append("</td></tr>");
            }
        }

        var metricRows = new StringBuilder();
        if (result["metrics"] is JsonArray metrics)
        {
            foreach (var metric in metrics.OfType<JsonObject>())
            {
                metricRows.Append("<tr><td>").Append(H(BenchmarkEngine.String(metric["category"])))
                    .Append("</td><td>").Append(H(BenchmarkEngine.String(metric["name"])))
                    .Append("</td><td>").Append(H(CsvValue(metric["value"])))
                    .Append("</td><td>").Append(H(BenchmarkEngine.String(metric["unit"])))
                    .Append("</td><td>").Append(H(BenchmarkEngine.String(metric["status"])))
                    .Append("</td><td>").Append(H(CsvValue(metric["stdev_pct"])))
                    .Append("</td></tr>");
            }
        }

        var global = scores?["global_score"] is null ? "N/A" : H(CsvValue(scores["global_score"]));
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>ESP32xBenchmark results</title><style>
            body{font:15px Segoe UI,Arial,sans-serif;max-width:1180px;margin:32px auto;padding:0 20px;background:#0b1220;color:#e5edf8}
            article{background:#121d30;border:1px solid #293953;border-radius:12px;padding:20px;margin:16px 0}
            h1,h2,th{color:#8ed8ff}.score{font-size:44px;font-weight:700}table{border-collapse:collapse;width:100%}
            th,td{padding:10px;text-align:left;border-bottom:1px solid #293953}.muted{color:#adbbcf}
            </style></head><body><h1>ESP32xBenchmark</h1><article>
            <h2>{{H(BenchmarkEngine.String(device["target"]))}}</h2><p>Family: {{H(BenchmarkEngine.String(device["family"]))}}
            · Class: {{H(BenchmarkEngine.String(device["comparability_class"]))}}</p>
            <div class="score">{{global}}<small> / 100</small></div>
            <p>Category coverage: {{FormatPercent(scores?["coverage"])}} of category weights · Reference: {{H(BenchmarkEngine.String(scores?["reference_profile"]))}}</p>
            <p class="muted">Coverage is the share of benchmark category weights represented by stable, matching measurements. The ESP32 classic reference is anchored at 20/100; scores are capped at 100. Cross-class scores use matching measured workloads only.</p>
            </article><article><h2>Category scores</h2><table><thead><tr><th>Category</th><th>Index</th><th>Weight</th></tr></thead>
            <tbody>{{categoryRows}}</tbody></table></article><article><h2>Raw measurements</h2><table><thead>
            <tr><th>Category</th><th>Metric</th><th>Value</th><th>Unit</th><th>Status</th><th>Variation %</th></tr></thead>
            <tbody>{{metricRows}}</tbody></table></article></body></html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }

    private static void WriteComparisonCsv(JsonObject comparison, string path)
    {
        var categories = Strings(comparison["common_categories"]);
        var builder = new StringBuilder();
        AppendCsvRow(builder, new[] { "target", "family", "comparability_class", "global_score", "coverage" }
            .Concat(categories).ToArray());
        if (comparison["results"] is JsonArray results)
        {
            foreach (var result in results.OfType<JsonObject>())
            {
                var categoryScores = result["categories"] as JsonObject;
                AppendCsvRow(builder,
                    new[]
                    {
                        BenchmarkEngine.String(result["target"]),
                        BenchmarkEngine.String(result["family"]),
                        BenchmarkEngine.String(result["comparability_class"]),
                        CsvValue(result["global_score"]),
                        CsvValue(result["coverage"])
                    }.Concat(categories.Select(category =>
                        CsvValue(categoryScores?[category]?["score"]))).ToArray());
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteComparisonHtml(JsonObject comparison, string path)
    {
        var categories = Strings(comparison["common_categories"]);
        var header = string.Join("", categories.Select(category => $"<th>{H(category)}</th>"));
        var rows = new StringBuilder();
        if (comparison["results"] is JsonArray results)
        {
            foreach (var result in results.OfType<JsonObject>())
            {
                var scores = result["categories"] as JsonObject;
                rows.Append("<tr><td>").Append(H(BenchmarkEngine.String(result["target"])))
                    .Append("</td><td>").Append(H(BenchmarkEngine.String(result["family"])))
                    .Append("</td><td>").Append(H(BenchmarkEngine.String(result["comparability_class"])))
                    .Append("</td><td>").Append(H(CsvValue(result["global_score"])))
                    .Append("</td><td>").Append(FormatPercent(result["coverage"])).Append("</td>");
                foreach (var category in categories)
                {
                    rows.Append("<td>").Append(H(CsvValue(scores?[category]?["score"]))).Append("</td>");
                }

                rows.Append("</tr>");
            }
        }

        var commonMetrics = string.Join(", ", Strings(comparison["common_metrics"]).Select(H));
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>ESP32xBenchmark comparison</title><style>
            body{font:15px Segoe UI,Arial,sans-serif;max-width:1180px;margin:32px auto;padding:0 20px;background:#0b1220;color:#e5edf8}
            article{background:#121d30;border:1px solid #293953;border-radius:12px;padding:20px;margin:16px 0}
            h1,h2,th{color:#8ed8ff}table{border-collapse:collapse;width:100%}th,td{padding:10px;text-align:left;border-bottom:1px solid #293953}
            .muted{color:#adbbcf}</style></head><body><h1>Shared-reference board comparison</h1><article>
            <p>Reference: <b>{{H(BenchmarkEngine.String(comparison["reference_profile"]))}}</b>
            ({{H(BenchmarkEngine.String(comparison["reference_class"]))}}); common category coverage: {{FormatPercent(comparison["coverage"])}}.</p>
            <p>Common stable metrics: {{commonMetrics}}</p><p class="muted">{{H(BenchmarkEngine.String(comparison["interpretation"]))}}</p>
            </article><article><table><thead><tr><th>Target</th><th>Family</th><th>Class</th><th>Common score /100</th><th>Coverage</th>{{header}}</tr></thead>
            <tbody>{{rows}}</tbody></table></article></body></html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }

    private static string[] Strings(JsonNode? array) =>
        array is JsonArray values
            ? values.Select(BenchmarkEngine.String).ToArray()
            : [];

    private static string CsvValue(JsonNode? node) =>
        node is null ? "" : node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString();

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string FormatPercent(JsonNode? node) =>
        BenchmarkEngine.TryNumber(node, out var value)
            ? (value * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
            : "0%";

    private static void AppendCsvRow(StringBuilder builder, params string[] fields)
    {
        builder.AppendLine(string.Join(",", fields.Select(field =>
            "\"" + (field ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
    }
}

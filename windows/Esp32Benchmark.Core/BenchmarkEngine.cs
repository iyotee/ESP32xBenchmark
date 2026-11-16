using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esp32Benchmark.Core;

/// <summary>Computes classic-anchored desktop scores and common-metric comparisons.</summary>
public static class BenchmarkEngine
{
    public static readonly IReadOnlyDictionary<string, double> CategoryWeights =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["cpu"] = 0.20,
            ["memory"] = 0.18,
            ["connectivity"] = 0.20,
            ["power"] = 0.18,
            ["peripherals"] = 0.10,
            ["rtos"] = 0.10,
            ["ai"] = 0.04
        };

    public const double MaxStdevPercent = 25.0;
    public const double ReferenceScore = 20.0;
    public const double MaximumScore = 100.0;

    public static JsonObject Score(
        JsonObject result,
        JsonObject referenceProfile,
        bool allowCrossClass = false,
        ISet<string>? metricNames = null)
    {
        var device = result["device"] as JsonObject
            ?? throw new InvalidDataException("Result is missing device metadata.");
        var resultClass = String(device["comparability_class"]);
        var referenceClass = String(referenceProfile["comparability_class"]);
        if (string.IsNullOrEmpty(resultClass))
        {
            throw new InvalidDataException("Result is missing comparability_class metadata.");
        }

        var crossClass = !StringComparer.Ordinal.Equals(resultClass, referenceClass);
        if (crossClass && !allowCrossClass)
        {
            throw new InvalidDataException(
                "Result and reference profile must have the same comparability_class.");
        }

        var referenceMetrics = referenceProfile["metrics"] as JsonObject
            ?? throw new InvalidDataException("Invalid reference profile metric format.");
        var measuredMetrics = result["metrics"] as JsonArray
            ?? throw new InvalidDataException("Invalid result metric format.");

        var categoryValues = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var unstable = new List<string>();
        var unmatched = new List<string>();
        foreach (var node in measuredMetrics)
        {
            if (node is not JsonObject metric)
            {
                throw new InvalidDataException("Each measurement must be an object.");
            }

            if (!StringComparer.Ordinal.Equals(String(metric["status"]), "ok"))
            {
                continue;
            }

            var category = String(metric["category"]);
            var name = String(metric["name"]);
            if (!CategoryWeights.ContainsKey(category) || string.IsNullOrEmpty(name))
            {
                throw new InvalidDataException(
                    "Scored measurements need a known category and metric name.");
            }

            if (metricNames is not null && !metricNames.Contains(name))
            {
                continue;
            }

            if (referenceMetrics[name] is not JsonObject reference ||
                !StringComparer.Ordinal.Equals(String(reference["category"]), category) ||
                !StringComparer.Ordinal.Equals(String(reference["direction"]), String(metric["direction"])) ||
                (crossClass &&
                 !StringComparer.Ordinal.Equals(String(reference["unit"]), String(metric["unit"]))))
            {
                unmatched.Add(name);
                continue;
            }

            var direction = String(metric["direction"]);
            if (direction is not ("higher" or "lower"))
            {
                throw new InvalidDataException($"{name}.direction must be 'higher' or 'lower'.");
            }

            var stdev = OptionalNumber(metric["stdev_pct"], $"{name}.stdev_pct");
            if (stdev is not null && stdev > MaxStdevPercent)
            {
                unstable.Add(name);
                continue;
            }

            var observed = PositiveNumber(metric["value"], $"{name}.value");
            var baseline = PositiveNumber(reference["value"], $"{name}.reference");
            var ratio = direction == "higher" ? observed / baseline : baseline / observed;
            if (!double.IsFinite(ratio))
            {
                continue;
            }

            if (!categoryValues.TryGetValue(category, out var values))
            {
                values = [];
                categoryValues.Add(category, values);
            }

            values.Add(new JsonObject
            {
                ["name"] = name,
                ["value"] = Round(Math.Clamp(ratio * ReferenceScore, 0, MaximumScore), 2),
                ["observed"] = observed,
                ["reference"] = baseline,
                ["unit"] = String(metric["unit"]) is { Length: > 0 } unit
                    ? unit
                    : String(reference["unit"])
            });
        }

        var categories = new JsonObject();
        double availableWeight = 0;
        double weightedSum = 0;
        foreach (var (category, values) in categoryValues)
        {
            var score = Round(values.Average(value => Number(value["value"])), 2);
            var weight = CategoryWeights[category];
            availableWeight += weight;
            weightedSum += score * weight;
            categories[category] = new JsonObject
            {
                ["score"] = score,
                ["weight"] = weight,
                ["metrics"] = new JsonArray(values.Select(value => (JsonNode?)value).ToArray())
            };
        }

        var excluded = new JsonArray(
            CategoryWeights.Keys
                .Where(category => !categoryValues.ContainsKey(category))
                .Select(category => (JsonNode?)JsonValue.Create(category))
                .ToArray());
        var totalWeight = CategoryWeights.Values.Sum();
        return new JsonObject
        {
            ["reference_profile"] = String(referenceProfile["profile_id"]) is { Length: > 0 } id
                ? id
                : "unnamed",
            ["result_class"] = resultClass,
            ["reference_class"] = referenceClass,
            ["cross_class"] = crossClass,
            ["score_model"] = "classic-anchor-weighted-score-v2",
            ["global_score"] = availableWeight > 0
                ? JsonValue.Create(Round(weightedSum / availableWeight, 2))
                : null,
            ["score_scale"] = "reference=20; maximum=100; scores are capped",
            ["coverage"] = Round(availableWeight / totalWeight, 4),
            ["available_weight"] = Round(availableWeight, 4),
            ["categories"] = categories,
            ["excluded_categories"] = excluded,
            ["unstable_metrics"] = new JsonArray(
                unstable.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["unmatched_metrics"] = new JsonArray(
                unmatched.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["interpretation"] = crossClass
                ? "Cross-class score uses matching metrics and units only; it is a workload " +
                  "comparison, not a same-class baseline. Power requires an external current " +
                  "monitor; AI/TinyML requires a supported model/runtime."
                : "Power requires an external current monitor; AI/TinyML requires a supported " +
                  "model/runtime. Missing categories are excluded and coverage is reported."
        };
    }

    public static JsonObject Compare(IReadOnlyList<JsonObject> results, JsonObject referenceProfile)
    {
        if (results.Count < 2)
        {
            throw new InvalidDataException("At least two results are required for a comparison.");
        }

        if (string.IsNullOrEmpty(String(referenceProfile["comparability_class"])))
        {
            throw new InvalidDataException("Reference profile has no comparability_class metadata.");
        }

        var references = referenceProfile["metrics"] as JsonObject
            ?? throw new InvalidDataException("Invalid reference profile metric format.");
        var common = new HashSet<string>(references.Select(item => item.Key), StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result["device"] is not JsonObject device ||
                string.IsNullOrEmpty(String(device["comparability_class"])))
            {
                throw new InvalidDataException(
                    "Each result must have device comparability metadata.");
            }

            if (result["metrics"] is not JsonArray metrics)
            {
                throw new InvalidDataException("Each result must have a metrics list.");
            }

            var measured = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in metrics)
            {
                if (node is not JsonObject metric ||
                    !StringComparer.Ordinal.Equals(String(metric["status"]), "ok"))
                {
                    continue;
                }

                var name = String(metric["name"]);
                if (name.Length == 0 || references[name] is not JsonObject reference)
                {
                    continue;
                }

                if (!StringComparer.Ordinal.Equals(String(metric["category"]), String(reference["category"])) ||
                    !StringComparer.Ordinal.Equals(String(metric["direction"]), String(reference["direction"])) ||
                    !StringComparer.Ordinal.Equals(String(metric["unit"]), String(reference["unit"])))
                {
                    continue;
                }

                var stdev = OptionalNumber(metric["stdev_pct"], $"{name}.stdev_pct");
                if (stdev is not null && stdev > MaxStdevPercent)
                {
                    continue;
                }

                _ = PositiveNumber(metric["value"], $"{name}.value");
                measured.Add(name);
            }

            common.IntersectWith(measured);
        }

        if (common.Count == 0)
        {
            throw new InvalidDataException(
                "The results have no stable, matching metrics in common with the reference.");
        }

        var commonNames = common.Order(StringComparer.Ordinal).ToArray();
        var scoredResults = new JsonArray();
        foreach (var result in results)
        {
            var scores = Score(result, referenceProfile, allowCrossClass: true, common);
            var device = (JsonObject)result["device"]!;
            var categories = scores["categories"]!.DeepClone();
            scoredResults.Add(new JsonObject
            {
                ["target"] = String(device["target"]) is { Length: > 0 } target ? target : "unknown",
                ["family"] = String(device["family"]) is { Length: > 0 } family ? family : "unknown",
                ["comparability_class"] = String(device["comparability_class"]),
                ["global_score"] = scores["global_score"]?.DeepClone(),
                ["coverage"] = scores["coverage"]?.DeepClone(),
                ["categories"] = categories
            });
        }

        var commonCategories = commonNames
            .Select(name => String(((JsonObject)references[name]!)["category"]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["comparison_model"] = "common-metrics-classic-anchor-score-v2",
            ["reference_profile"] = String(referenceProfile["profile_id"]) is { Length: > 0 } id
                ? id
                : "unnamed",
            ["reference_class"] = String(referenceProfile["comparability_class"]),
            ["common_categories"] = new JsonArray(
                commonCategories.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["common_metrics"] = new JsonArray(
                commonNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["coverage"] = Round(
                commonCategories.Sum(category => CategoryWeights[category]) /
                CategoryWeights.Values.Sum(), 4),
            ["results"] = scoredResults,
            ["interpretation"] =
                "All devices use the same reference and the same intersection of stable " +
                "metrics. This enables cross-class comparison for these workloads only; " +
                "it does not make unsupported categories equivalent."
        };
    }

    public static JsonObject MakeReferenceProfile(JsonObject result, string profileId)
    {
        var device = result["device"] as JsonObject
            ?? throw new InvalidDataException("Result has no comparability_class metadata.");
        var comparabilityClass = String(device["comparability_class"]);
        if (comparabilityClass.Length == 0)
        {
            throw new InvalidDataException("Result has no comparability_class metadata.");
        }

        var metrics = new JsonObject();
        if (result["metrics"] is JsonArray sourceMetrics)
        {
            foreach (var node in sourceMetrics)
            {
                if (node is not JsonObject metric ||
                    !StringComparer.Ordinal.Equals(String(metric["status"]), "ok"))
                {
                    continue;
                }

                var category = String(metric["category"]);
                if (!CategoryWeights.ContainsKey(category))
                {
                    continue;
                }

                var stdev = OptionalNumber(metric["stdev_pct"], "metric.stdev_pct");
                if (stdev is not null && stdev > MaxStdevPercent)
                {
                    continue;
                }

                double value;
                try
                {
                    value = PositiveNumber(metric["value"], "metric.value");
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                var name = String(metric["name"]);
                var direction = String(metric["direction"]);
                if (name.Length == 0 || direction is not ("higher" or "lower"))
                {
                    continue;
                }

                metrics[name] = new JsonObject
                {
                    ["category"] = category,
                    ["value"] = value,
                    ["unit"] = String(metric["unit"]),
                    ["direction"] = direction
                };
            }
        }

        if (metrics.Count == 0)
        {
            throw new InvalidDataException("No valid measured metrics are available for calibration.");
        }

        var runIds = new JsonArray();
        if (result["run_summaries"] is JsonArray summaries)
        {
            foreach (var run in summaries.OfType<JsonObject>())
            {
                runIds.Add(run["run_id"]?.DeepClone());
            }
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["profile_id"] = profileId,
            ["reference_run_id"] = result["run_id"]?.DeepClone(),
            ["reference_run_ids"] = runIds,
            ["calibration_run_count"] = result["run_count"]?.DeepClone() ?? JsonValue.Create(1),
            ["calibrated_at"] = result["captured_at"]?.DeepClone(),
            ["comparability_class"] = comparabilityClass,
            ["reference_device"] = device.DeepClone(),
            ["reference_host"] = result["host"]?.DeepClone(),
            ["metrics"] = metrics,
            ["notice"] = "Measured calibration run; reference values are not manufacturer specifications."
        };
    }

    public static JsonObject AggregateRuns(IReadOnlyList<JsonObject> runs)
    {
        if (runs.Count == 0)
        {
            throw new InvalidDataException("At least one complete run is required.");
        }

        var firstDevice = runs[0]["device"] as JsonObject
            ?? throw new InvalidDataException("Run is missing device metadata.");
        foreach (var run in runs.Skip(1))
        {
            var device = run["device"] as JsonObject
                ?? throw new InvalidDataException("Run is missing device metadata.");
            if (!StringComparer.Ordinal.Equals(String(device["comparability_class"]),
                    String(firstDevice["comparability_class"])) ||
                !StringComparer.Ordinal.Equals(String(device["target"]), String(firstDevice["target"])))
            {
                throw new InvalidDataException(
                    "Repeated captures must come from the same target and class.");
            }
        }

        var metricsByName = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (run["metrics"] is not JsonArray runMetrics)
            {
                continue;
            }

            foreach (var metric in runMetrics.OfType<JsonObject>())
            {
                var name = String(metric["name"]);
                if (name.Length == 0)
                {
                    continue;
                }

                if (!metricsByName.TryGetValue(name, out var measurements))
                {
                    measurements = [];
                    metricsByName.Add(name, measurements);
                }

                measurements.Add(metric);
            }
        }

        var aggregatedMetrics = new JsonArray();
        foreach (var (name, measurements) in metricsByName)
        {
            var successful = measurements
                .Where(metric =>
                    StringComparer.Ordinal.Equals(String(metric["status"]), "ok") &&
                    TryNumber(metric["value"], out _))
                .ToArray();
            if (successful.Length == 0)
            {
                aggregatedMetrics.Add(measurements[0].DeepClone());
                continue;
            }

            if (successful.Length != runs.Count)
            {
                var partial = (JsonObject)successful[0].DeepClone();
                partial["value"] = null;
                partial["status"] = "partial";
                partial["reason"] = "metric was not measured successfully in every repeated run";
                partial["successful_runs"] = successful.Length;
                partial["total_runs"] = runs.Count;
                aggregatedMetrics.Add(partial);
                continue;
            }

            var values = successful.Select(metric => Number(metric["value"])).Order().ToArray();
            var median = values.Length % 2 == 0
                ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0
                : values[values.Length / 2];
            var mean = values.Average();
            var betweenRunStdev = mean == 0
                ? 0
                : Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / values.Length) *
                  100.0 / Math.Abs(mean);
            var withinRun = successful
                .Select(metric => OptionalNumber(metric["stdev_pct"], $"{name}.stdev_pct") ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            var aggregated = (JsonObject)successful[0].DeepClone();
            aggregated["value"] = median;
            aggregated["median"] = median;
            aggregated["min"] = values[0];
            aggregated["max"] = values[^1];
            aggregated["stdev_pct"] = Math.Max(betweenRunStdev, withinRun);
            aggregated["within_run_stdev_pct"] = withinRun;
            aggregated["between_run_stdev_pct"] = betweenRunStdev;
            aggregated["samples"] = successful.Sum(metric =>
                (int)(OptionalNumber(metric["samples"], $"{name}.samples") ?? 1));
            aggregatedMetrics.Add(aggregated);
        }

        var summaries = new JsonArray();
        foreach (var run in runs)
        {
            var summaryMetrics = new JsonArray();
            if (run["metrics"] is JsonArray runMetrics)
            {
                foreach (var metric in runMetrics.OfType<JsonObject>())
                {
                    summaryMetrics.Add(new JsonObject
                    {
                        ["name"] = String(metric["name"]),
                        ["value"] = metric["value"]?.DeepClone()
                    });
                }
            }

            summaries.Add(new JsonObject
            {
                ["run_id"] = run["run_id"]?.DeepClone() ?? JsonValue.Create(""),
                ["captured_at"] = run["captured_at"]?.DeepClone(),
                ["metrics"] = summaryMetrics
            });
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["run_id"] = runs[0]["run_id"]?.DeepClone() ?? JsonValue.Create(""),
            ["run_count"] = runs.Count,
            ["device"] = firstDevice.DeepClone(),
            ["metrics"] = aggregatedMetrics,
            ["run_summaries"] = summaries,
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };
    }

    public static string String(JsonNode? value) => value is JsonValue
        ? value.ToString()
        : string.Empty;

    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<double>(node.ToJsonString());
            if (!double.IsFinite(parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double Number(JsonNode? node) =>
        TryNumber(node, out var number)
            ? number
            : throw new InvalidDataException("Expected a JSON number.");

    private static double? OptionalNumber(JsonNode? node, string label)
    {
        if (node is null)
        {
            return null;
        }

        if (!TryNumber(node, out var number) || number < 0)
        {
            throw new InvalidDataException($"{label} must be a finite non-negative number.");
        }

        return number;
    }

    private static double PositiveNumber(JsonNode? node, string label)
    {
        var value = Number(node);
        if (value <= 0)
        {
            throw new InvalidDataException(
                $"{label} must be a finite number greater than zero.");
        }

        return value;
    }

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Esp32Benchmark.Core;

/// <summary>Consumes the existing line-delimited JSON firmware protocol.</summary>
public sealed class SerialCaptureAccumulator(int expectedRuns)
{
    private JsonObject? _device;
    private string _runId = "";
    private List<JsonObject> _metrics = [];
    private readonly List<JsonObject> _runs = [];

    public int ExpectedRuns { get; } = expectedRuns > 0
        ? expectedRuns
        : throw new ArgumentOutOfRangeException(nameof(expectedRuns));

    public int CompletedRuns => _runs.Count;
    public bool IsComplete => CompletedRuns >= ExpectedRuns;

    public bool ConsumeLine(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line.Trim());
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject record)
        {
            return false;
        }

        switch (BenchmarkEngine.String(record["type"]))
        {
            case "run_start":
                _device = record["device"] is JsonObject device
                    ? (JsonObject)device.DeepClone()
                    : throw new InvalidDataException(
                        "Serial run_start record is missing device metadata.");
                _metrics = [];
                _runId = BenchmarkEngine.String(record["run_id"]);
                return false;

            case "metric" when _device is not null:
                _metrics.Add((JsonObject)record.DeepClone());
                return false;

            case "run_complete" when _device is not null:
                _runs.Add(new JsonObject
                {
                    ["run_id"] = _runId,
                    ["device"] = _device,
                    ["metrics"] = new JsonArray(_metrics.Select(metric => (JsonNode?)metric).ToArray()),
                    ["captured_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                });
                _device = null;
                _metrics = [];
                return true;

            default:
                return false;
        }
    }

    public JsonObject BuildResult()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException(
                $"Received {CompletedRuns} of {ExpectedRuns} complete benchmark runs.");
        }

        return BenchmarkEngine.AggregateRuns(_runs.Take(ExpectedRuns).ToArray());
    }

    public static JsonObject? ReadDeviceIdentity(string line)
    {
        try
        {
            if (JsonNode.Parse(line.Trim()) is JsonObject record &&
                BenchmarkEngine.String(record["type"]) == "run_start" &&
                record["device"] is JsonObject device)
            {
                return (JsonObject)device.DeepClone();
            }
        }
        catch (JsonException)
        {
            // ROM boot messages and regular console text are expected on this stream.
        }

        return null;
    }
}

public static class BoardEnvironmentRecommender
{
    public static (string? Environment, string Explanation) RecommendDetectedChip(string chipName)
    {
        var chip = new string(chipName.Where(char.IsLetterOrDigit).ToArray())
            .ToUpperInvariant();

        if (chip.StartsWith("ESP8266", StringComparison.Ordinal))
        {
            return ("esp8266", "The ESP8266 ROM bootloader identified this chip.");
        }

        if (chip.StartsWith("ESP32S2", StringComparison.Ordinal))
        {
            return ("esp32s2", "The ESP32-S2 ROM bootloader identified this chip.");
        }

        if (chip.StartsWith("ESP32S3", StringComparison.Ordinal))
        {
            return ("esp32s3", "The ESP32-S3 ROM bootloader identified this chip.");
        }

        if (chip.StartsWith("ESP32C3", StringComparison.Ordinal))
        {
            return ("esp32c3", "The ESP32-C3 ROM bootloader identified this chip.");
        }

        if (chip.StartsWith("ESP32C6", StringComparison.Ordinal))
        {
            return ("esp32c6", "The ESP32-C6 ROM bootloader identified this chip.");
        }

        if (chip == "ESP32" ||
            chip.StartsWith("ESP32D0WD", StringComparison.Ordinal) ||
            chip.StartsWith("ESP32PICO", StringComparison.Ordinal) ||
            chip.StartsWith("ESP32U4WD", StringComparison.Ordinal))
        {
            return ("esp32dev", "The ESP32 ROM bootloader identified a classic ESP32 chip.");
        }

        return (null, $"No bundled PlatformIO environment supports detected chip '{chipName}'.");
    }

    public static (string? Environment, string Explanation) Recommend(JsonObject? device)
    {
        if (device is null)
        {
            return (null, "Identify benchmark firmware for an environment recommendation.");
        }

        var identity = string.Join(' ',
            BenchmarkEngine.String(device["family"]),
            BenchmarkEngine.String(device["target"]),
            BenchmarkEngine.String(device["architecture"])).ToLowerInvariant();

        if (identity.Contains("esp8266", StringComparison.Ordinal) ||
            identity.Contains("lx106", StringComparison.Ordinal))
        {
            return ("esp8266", "Firmware identifies the ESP8266 family.");
        }

        var mappings = new (string Token, string Environment)[]
        {
            ("esp32-s2", "esp32s2"),
            ("esp32s2", "esp32s2"),
            ("esp32-s3", "esp32s3"),
            ("esp32s3", "esp32s3"),
            ("esp32-c3", "esp32c3"),
            ("esp32c3", "esp32c3"),
            ("esp32-c6", "esp32c6"),
            ("esp32c6", "esp32c6")
        };
        foreach (var (token, environment) in mappings)
        {
            if (identity.Contains(token, StringComparison.Ordinal))
            {
                return (environment, $"Firmware identifies {token.ToUpperInvariant()}.");
            }
        }

        if (identity.Contains("esp32", StringComparison.Ordinal) ||
            identity.Contains("xtensa", StringComparison.Ordinal))
        {
            return ("esp32dev", "Firmware identifies a classic ESP32-family target.");
        }

        return (null, "Board identity was received, but no supported PlatformIO environment matched it.");
    }
}

using System.Text.Json.Nodes;
using Esp32Benchmark.Core;
using Xunit;

namespace Esp32Benchmark.Tests;

public sealed class BenchmarkParityTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void CrossBoardComparisonMatchesPublishedPythonComparisonSample()
    {
        var profile = Read("esp32-classic-v1.json");
        var classic = Read("esp32-classic-v1-verification.json");
        var c3 = Read("esp32-c3-verification.json");
        var esp8266 = Read("esp8266-verification.json");
        var expected = Read("python-comparison.json");

        var comparison = BenchmarkEngine.Compare([classic, c3, esp8266], profile);

        Assert.Equal(expected["common_metrics"]!.ToJsonString(),
            comparison["common_metrics"]!.ToJsonString());
        Assert.Equal(expected["common_categories"]!.ToJsonString(),
            comparison["common_categories"]!.ToJsonString());
        Assert.Equal((double)expected["coverage"]!, (double)comparison["coverage"]!);

        var expectedRows = expected["results"]!.AsArray();
        var actualRows = comparison["results"]!.AsArray();
        Assert.Equal(expectedRows.Count, actualRows.Count);
        for (var index = 0; index < expectedRows.Count; index++)
        {
            Assert.Equal((double)expectedRows[index]!["global_score"]!,
                (double)actualRows[index]!["global_score"]!);
            Assert.Equal((double)expectedRows[index]!["coverage"]!,
                (double)actualRows[index]!["coverage"]!);
            AssertCategoryScores(expectedRows[index]!["categories"]!.AsObject(),
                actualRows[index]!["categories"]!.AsObject());
        }

        Assert.Equal(new[] { 100.0, 58.61, 22.67 },
            actualRows.Select(item => (double)item!["global_score"]!).ToArray());
    }

    [Fact]
    public void SameClassScoreMatchesReferenceAndLowerIsBetterDirection()
    {
        var profile = new JsonObject
        {
            ["profile_id"] = "rtos-baseline",
            ["comparability_class"] = "classic_wifi_ble",
            ["metrics"] = new JsonObject
            {
                ["task_handoff_us"] = new JsonObject
                {
                    ["category"] = "rtos",
                    ["value"] = 20.0,
                    ["unit"] = "us",
                    ["direction"] = "lower"
                }
            }
        };
        var result = new JsonObject
        {
            ["device"] = new JsonObject { ["comparability_class"] = "classic_wifi_ble" },
            ["metrics"] = new JsonArray(new JsonObject
            {
                ["category"] = "rtos",
                ["name"] = "task_handoff_us",
                ["value"] = 10.0,
                ["unit"] = "us",
                ["direction"] = "lower",
                ["status"] = "ok"
            })
        };

        var score = BenchmarkEngine.Score(result, profile);

        Assert.Equal(200.0, (double)score["global_score"]!);
        Assert.Equal(0.10, (double)score["coverage"]!);
        Assert.Throws<InvalidDataException>(() =>
            BenchmarkEngine.Score(result, new JsonObject
            {
                ["comparability_class"] = "esp8266_wifi",
                ["metrics"] = new JsonObject()
            }));
    }

    [Fact]
    public void ScoringExcludesMetricsAboveThePythonStabilityLimit()
    {
        var profile = new JsonObject
        {
            ["profile_id"] = "cpu-baseline",
            ["comparability_class"] = "classic_wifi_ble",
            ["metrics"] = new JsonObject
            {
                ["integer_mix_miter_s"] = new JsonObject
                {
                    ["category"] = "cpu",
                    ["value"] = 10.0,
                    ["unit"] = "Miter/s",
                    ["direction"] = "higher"
                }
            }
        };
        var result = new JsonObject
        {
            ["device"] = new JsonObject { ["comparability_class"] = "classic_wifi_ble" },
            ["metrics"] = new JsonArray(new JsonObject
            {
                ["category"] = "cpu",
                ["name"] = "integer_mix_miter_s",
                ["value"] = 10.0,
                ["unit"] = "Miter/s",
                ["direction"] = "higher",
                ["status"] = "ok",
                ["stdev_pct"] = 25.01
            })
        };

        var score = BenchmarkEngine.Score(result, profile);

        Assert.Null(score["global_score"]);
        Assert.Equal(0.0, (double)score["coverage"]!);
        Assert.Equal("integer_mix_miter_s",
            score["unstable_metrics"]![0]!.GetValue<string>());
    }

    [Fact]
    public void SerialProtocolIgnoresBootTextAndAggregatesCompletedRuns()
    {
        var parser = new SerialCaptureAccumulator(expectedRuns: 2);
        Assert.False(parser.ConsumeLine("bootloader banner"));
        Assert.False(parser.ConsumeLine("""
            {"type":"run_start","run_id":"one","device":{"target":"ESP32-D0WD-V3","family":"esp32","comparability_class":"classic_wifi_ble"}}
            """));
        parser.ConsumeLine("""{"type":"metric","category":"cpu","name":"integer_mix_miter_s","value":10,"unit":"Miter/s","direction":"higher","status":"ok","stdev_pct":1}""");
        Assert.True(parser.ConsumeLine("""{"type":"run_complete"}"""));
        Assert.False(parser.ConsumeLine("""
            {"type":"run_start","run_id":"two","device":{"target":"ESP32-D0WD-V3","family":"esp32","comparability_class":"classic_wifi_ble"}}
            """));
        parser.ConsumeLine("""{"type":"metric","category":"cpu","name":"integer_mix_miter_s","value":20,"unit":"Miter/s","direction":"higher","status":"ok","stdev_pct":1}""");
        Assert.True(parser.ConsumeLine("""{"type":"run_complete"}"""));

        var result = parser.BuildResult();
        var metric = result["metrics"]![0]!;
        Assert.Equal(2, (int)result["run_count"]!);
        Assert.Equal(15.0, (double)metric["value"]!);
        Assert.Equal(10.0, (double)metric["min"]!);
        Assert.Equal(20.0, (double)metric["max"]!);
    }

    [Theory]
    [InlineData("ESP32-D0WD-V3", "esp32", "Xtensa", "esp32dev")]
    [InlineData("ESP32-C3", "esp32c3", "RISC-V", "esp32c3")]
    [InlineData("ESP8266EX", "esp8266", "Xtensa-LX106", "esp8266")]
    public void BoardIdentitySuggestsExistingPlatformIoEnvironment(
        string target, string family, string architecture, string expected)
    {
        var device = new JsonObject
        {
            ["target"] = target,
            ["family"] = family,
            ["architecture"] = architecture
        };

        Assert.Equal(expected, BoardEnvironmentRecommender.Recommend(device).Environment);
    }

    [Fact]
    public void GeneratedComparisonReportsRemainReadable()
    {
        var profile = Read("esp32-classic-v1.json");
        var comparison = BenchmarkEngine.Compare(
            [Read("esp32-classic-v1-verification.json"), Read("esp32-c3-verification.json")],
            profile);
        var directory = Path.Combine(Path.GetTempPath(), "esp32x-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            ReportWriter.WriteComparison(comparison, directory);
            Assert.True(File.Exists(Path.Combine(directory, "comparison.json")));
            Assert.Contains("Common index",
                File.ReadAllText(Path.Combine(directory, "comparison.html")));
            Assert.Contains("global_score",
                File.ReadAllText(Path.Combine(directory, "comparison.csv")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static JsonObject Read(string filename) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, filename)))!.AsObject();

    private static void AssertCategoryScores(JsonObject expected, JsonObject actual)
    {
        Assert.Equal(expected.Select(item => item.Key).Order(),
            actual.Select(item => item.Key).Order());
        foreach (var (category, expectedNode) in expected)
        {
            var expectedCategory = expectedNode!.AsObject();
            var actualCategory = actual[category]!.AsObject();
            Assert.Equal((double)expectedCategory["score"]!, (double)actualCategory["score"]!);
            Assert.Equal((double)expectedCategory["weight"]!, (double)actualCategory["weight"]!);
            var expectedMetrics = expectedCategory["metrics"]!.AsArray();
            var actualMetrics = actualCategory["metrics"]!.AsArray();
            Assert.Equal(expectedMetrics.Count, actualMetrics.Count);
            for (var index = 0; index < expectedMetrics.Count; index++)
            {
                var expectedMetric = expectedMetrics[index]!.AsObject();
                var actualMetric = actualMetrics[index]!.AsObject();
                foreach (var key in new[] { "name", "unit" })
                {
                    Assert.Equal(expectedMetric[key]?.ToString(), actualMetric[key]?.ToString());
                }

                foreach (var key in new[] { "value", "observed", "reference" })
                {
                    Assert.Equal((double)expectedMetric[key]!, (double)actualMetric[key]!);
                }
            }
        }
    }
}

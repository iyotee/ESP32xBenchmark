using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using Esp32Benchmark.Core;
using Xunit;

namespace Esp32Benchmark.Tests;

public sealed class BenchmarkParityTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void LocalizationCatalogsCoverDesktopTextAndListExplicitFallbacks()
    {
        var catalog = new LocalizationCatalog();
        var english = catalog.GetStrings(CultureInfo.GetCultureInfo("en-US"));
        var french = catalog.GetStrings(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal(english.Keys.Order(), french.Keys.Order());
        foreach (var fileName in new[] { "MainWindow.xaml", "MainWindow.xaml.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Fixtures, fileName));
            var keys = Regex.Matches(source,
                    "\"(Text\\.[A-Z][A-Za-z0-9]+)\"|\\{DynamicResource (Text\\.[A-Za-z0-9]+)\\}")
                .Select(match => match.Groups[1].Success
                    ? match.Groups[1].Value
                    : match.Groups[2].Value)
                .Distinct(StringComparer.Ordinal);
            Assert.All(keys, key => Assert.Contains(key, english.Keys));
        }

        foreach (var value in english.Values.Concat(french.Values))
        {
            _ = string.Format(CultureInfo.InvariantCulture, value, 0, 1, 2);
        }

        Assert.Equal(english["Text.Ready"], catalog.GetString(
            "Text.Ready", CultureInfo.GetCultureInfo("es-ES")));
        var spanish = catalog.GetLanguageOptions("Windows (automatic)", "English fallback")
            .Single(option => option.Name == "es-ES");
        Assert.False(spanish.HasTranslation);
        Assert.Contains("English fallback", spanish.DisplayName);
    }

    [Fact]
    public void DesktopPreferencesRoundTripThemeAndCulture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "esp32x-preferences-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            var preferences = new DesktopPreferences(ThemePreference.Light, "fr-FR");
            new DesktopPreferencesStore(path).Save(preferences);

            Assert.Equal(preferences, new DesktopPreferencesStore(path).Load());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DatedReleaseTagsValidateAndCompareByDateThenDailySequence()
    {
        Assert.True(DatedReleaseVersion.TryParseTag("V2026.11.16.1", out var first));
        Assert.True(DatedReleaseVersion.TryParseTag("V2026.11.16.2", out var second));
        Assert.True(DatedReleaseVersion.TryParseTag("V2026.11.17.1", out var nextDay));

        Assert.Equal(new DateOnly(2026, 11, 16), first.ReleaseDate);
        Assert.True(first.CompareTo(second) < 0);
        Assert.True(second.CompareTo(nextDay) < 0);
        Assert.Equal("V2026.11.16.1", first.ToString());
        Assert.False(DatedReleaseVersion.TryParseTag("V2026-11-16-1", out _));
        Assert.False(DatedReleaseVersion.TryParseTag("V2026.02.30.1", out _));
        Assert.False(DatedReleaseVersion.TryParseTag("V2026.11.16.0", out _));
    }

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
            var expectedScore = Math.Round(
                (double)expectedRows[index]!["global_score"]! * 0.2, 2);
            Assert.InRange((double)actualRows[index]!["global_score"]!,
                expectedScore - 0.02, expectedScore + 0.02);
            Assert.Equal((double)expectedRows[index]!["coverage"]!,
                (double)actualRows[index]!["coverage"]!);
            AssertCategoryScores(expectedRows[index]!["categories"]!.AsObject(),
                actualRows[index]!["categories"]!.AsObject());
        }

        var knownScores = new[] { 20.0, 11.72, 4.53 };
        for (var index = 0; index < knownScores.Length; index++)
        {
            Assert.InRange((double)actualRows[index]!["global_score"]!,
                knownScores[index] - 0.02, knownScores[index] + 0.02);
        }
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

        Assert.Equal(40.0, (double)score["global_score"]!);
        Assert.Equal(0.10, (double)score["coverage"]!);
        Assert.Throws<InvalidDataException>(() =>
            BenchmarkEngine.Score(result, new JsonObject
            {
                ["comparability_class"] = "esp8266_wifi",
                ["metrics"] = new JsonObject()
            }));
    }

    [Fact]
    public void CrossClassScoreUsesCompatibleMetricsAndIdentifiesItsScope()
    {
        var profile = new JsonObject
        {
            ["profile_id"] = "classic-baseline",
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
            ["device"] = new JsonObject { ["comparability_class"] = "low_cost_wifi_ble" },
            ["metrics"] = new JsonArray(new JsonObject
            {
                ["category"] = "cpu",
                ["name"] = "integer_mix_miter_s",
                ["value"] = 5.0,
                ["unit"] = "Miter/s",
                ["direction"] = "higher",
                ["status"] = "ok"
            })
        };

        var score = BenchmarkEngine.Score(result, profile, allowCrossClass: true);

        Assert.Equal(10.0, (double)score["global_score"]!);
        Assert.True((bool)score["cross_class"]!);
        Assert.Equal("low_cost_wifi_ble", score["result_class"]!.GetValue<string>());
        Assert.Equal("classic_wifi_ble", score["reference_class"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => BenchmarkEngine.Score(result, profile));
    }

    [Fact]
    public void CrossClassScoreExcludesMetricsWithMismatchedUnits()
    {
        var profile = new JsonObject
        {
            ["profile_id"] = "classic-baseline",
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
            ["device"] = new JsonObject { ["comparability_class"] = "low_cost_wifi_ble" },
            ["metrics"] = new JsonArray(new JsonObject
            {
                ["category"] = "cpu",
                ["name"] = "integer_mix_miter_s",
                ["value"] = 5.0,
                ["unit"] = "iterations/s",
                ["direction"] = "higher",
                ["status"] = "ok"
            })
        };

        var score = BenchmarkEngine.Score(result, profile, allowCrossClass: true);

        Assert.Null(score["global_score"]);
        Assert.Equal("integer_mix_miter_s",
            score["unmatched_metrics"]![0]!.GetValue<string>());
    }

    [Fact]
    public void ClassicReferenceIsTwentyAndScoresCapAtOneHundred()
    {
        var profile = new JsonObject
        {
            ["profile_id"] = "classic-baseline",
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
                ["status"] = "ok"
            })
        };

        Assert.Equal(20.0, (double)BenchmarkEngine.Score(result, profile)["global_score"]!);

        result["metrics"]![0]!["value"] = 25.0;
        Assert.Equal(50.0, (double)BenchmarkEngine.Score(result, profile)["global_score"]!);

        result["metrics"]![0]!["value"] = 100.0;
        Assert.Equal(100.0, (double)BenchmarkEngine.Score(result, profile)["global_score"]!);
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

    [Theory]
    [InlineData("ESP32-D0WD-V3", "esp32dev")]
    [InlineData("ESP32-C3", "esp32c3")]
    [InlineData("ESP32-S2", "esp32s2")]
    [InlineData("ESP32-S3", "esp32s3")]
    [InlineData("ESP32-C6", "esp32c6")]
    [InlineData("ESP8266EX", "esp8266")]
    public void RomChipDetectionSelectsMatchingPlatformIoEnvironment(
        string chipName, string expected)
    {
        Assert.Equal(expected, BoardEnvironmentRecommender.RecommendDetectedChip(chipName).Environment);
    }

    [Fact]
    public void RomChipDetectionDoesNotMisclassifyUnsupportedEsp32Variants()
    {
        Assert.Null(BoardEnvironmentRecommender.RecommendDetectedChip("ESP32-H2").Environment);
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
            Assert.Contains("Common score /100",
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
            var expectedScore = Math.Round((double)expectedCategory["score"]! * 0.2, 2);
            Assert.InRange((double)actualCategory["score"]!,
                expectedScore - 0.02, expectedScore + 0.02);
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
                    var expectedValue = (double)expectedMetric[key]!;
                    Assert.Equal(key == "value" ? Math.Round(expectedValue * 0.2, 2) : expectedValue,
                        (double)actualMetric[key]!);
                }
            }
        }
    }
}

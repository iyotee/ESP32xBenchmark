using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public sealed record HistoryEntry(
    string Id,
    string Target,
    string Family,
    string ComparabilityClass,
    string ProfileId,
    string CapturedAt,
    string FilePath,
    JsonObject Result)
{
    public string CapturedAtDisplay =>
        DateTimeOffset.TryParse(
            CapturedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var captured)
            ? captured.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : CapturedAt;
}

public sealed class HistoryRepository
{
    public const string DefaultProfileId = "esp32-classic-v1";

    private const string ProfileResourcePrefix = "Esp32Benchmark.Desktop.Profiles.";
    private static readonly string[] BundledProfileIds =
    [
        DefaultProfileId
    ];

    public HistoryRepository()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESP32xBenchmark");
        HistoryDirectory = Path.Combine(root, "history");
        ProfileDirectory = Path.Combine(root, "profiles");
        Directory.CreateDirectory(HistoryDirectory);
        Directory.CreateDirectory(ProfileDirectory);
        EnsureBundledProfiles();
    }

    public string HistoryDirectory { get; }
    public string ProfileDirectory { get; }

    public HistoryEntry SaveRun(JsonObject result, string profileId)
    {
        var device = result["device"] as JsonObject
            ?? throw new InvalidDataException("A saved benchmark result must contain device metadata.");
        var id = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        var path = Path.Combine(HistoryDirectory, id + ".json");
        ReportWriter.WriteJson(new JsonObject
        {
            ["history_id"] = id,
            ["profile_id"] = profileId,
            ["saved_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["result"] = result.DeepClone()
        }, path);
        return Entry(id, result, profileId, path);
    }

    public IReadOnlyList<HistoryEntry> GetRuns()
    {
        var entries = new List<HistoryEntry>();
        foreach (var path in Directory.EnumerateFiles(HistoryDirectory, "*.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject payload)
                {
                    continue;
                }

                var result = payload["result"] as JsonObject ?? payload;
                if (
                    result["device"] is not JsonObject)
                {
                    continue;
                }

                entries.Add(Entry(
                    Path.GetFileNameWithoutExtension(path),
                    result,
                    BenchmarkEngine.String(payload["profile_id"]) is { Length: > 0 } profile
                        ? profile
                        : BenchmarkEngine.String(result["scores"]?["reference_profile"]) is { Length: > 0 } scoredProfile
                            ? scoredProfile
                            : "unscored",
                    path));
            }
            catch (JsonException)
            {
                // Leave corrupt or interrupted files on disk for recovery, but don't break the history view.
            }
            catch (IOException)
            {
                // A just-written file can be skipped until the next refresh.
            }
        }

        return entries.OrderByDescending(entry => entry.CapturedAt, StringComparer.Ordinal).ToArray();
    }

    public void ImportResult(string sourcePath, string profileId)
    {
        if (JsonNode.Parse(File.ReadAllText(sourcePath)) is not JsonObject result ||
            result["device"] is not JsonObject ||
            result["metrics"] is not JsonArray)
        {
            throw new InvalidDataException("The selected JSON is not a benchmark result.");
        }

        SaveRun(result, profileId);
    }

    public void DeleteRun(HistoryEntry entry)
    {
        if (File.Exists(entry.FilePath))
        {
            File.Delete(entry.FilePath);
        }
    }

    public JsonObject LoadProfile(string profileId)
    {
        var path = Path.Combine(ProfileDirectory, SafeProfileFileName(profileId));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Reference profile '{profileId}' is not in the local library.");
        }

        return ParseProfile(File.ReadAllText(path));
    }

    public IReadOnlyList<string> GetProfileIds() =>
        Directory.EnumerateFiles(ProfileDirectory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void VerifyBundledProfiles()
    {
        foreach (var profileId in BundledProfileIds)
        {
            using var stream = OpenBundledProfile(profileId);
            using var reader = new StreamReader(stream);
            ParseProfile(reader.ReadToEnd());
        }
    }

    public string ImportProfile(string sourcePath)
    {
        var profile = ParseProfile(File.ReadAllText(sourcePath));
        var id = BenchmarkEngine.String(profile["profile_id"]);
        SaveProfile(profile);
        return id;
    }

    public string SaveProfile(JsonObject profile)
    {
        var profileId = BenchmarkEngine.String(profile["profile_id"]);
        if (profileId.Length == 0 || BenchmarkEngine.String(profile["comparability_class"]).Length == 0)
        {
            throw new InvalidDataException(
                "Reference profile must include profile_id and comparability_class.");
        }

        var path = Path.Combine(ProfileDirectory, SafeProfileFileName(profileId));
        ReportWriter.WriteJson(profile, path);
        return path;
    }

    private static JsonObject ParseProfile(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject profile ||
            profile["metrics"] is not JsonObject ||
            BenchmarkEngine.String(profile["profile_id"]).Length == 0 ||
            BenchmarkEngine.String(profile["comparability_class"]).Length == 0)
        {
            throw new InvalidDataException(
                "Reference profile JSON must include profile_id, comparability_class, and metrics.");
        }

        return profile;
    }

    private static string SafeProfileFileName(string profileId)
    {
        if (profileId.Length == 0 || !char.IsAsciiLetterOrDigit(profileId[0]) ||
            profileId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new InvalidDataException(
                "Profile IDs may contain only letters, numbers, dots, underscores, and hyphens.");
        }

        return profileId + ".json";
    }

    private void EnsureBundledProfiles()
    {
        foreach (var profileId in BundledProfileIds)
        {
            var destination = Path.Combine(ProfileDirectory, SafeProfileFileName(profileId));
            if (File.Exists(destination))
            {
                continue;
            }

            using var source = OpenBundledProfile(profileId);
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            source.CopyTo(target);
        }
    }

    private static Stream OpenBundledProfile(string profileId) =>
        typeof(HistoryRepository).Assembly.GetManifestResourceStream(
            ProfileResourcePrefix + profileId + ".json")
        ?? throw new InvalidDataException(
            $"The application is missing bundled reference profile '{profileId}'.");

    private static HistoryEntry Entry(string id, JsonObject result, string profileId, string path)
    {
        var device = (JsonObject)result["device"]!;
        return new HistoryEntry(
            id,
            BenchmarkEngine.String(device["target"]) is { Length: > 0 } target ? target : "Unknown target",
            BenchmarkEngine.String(device["family"]) is { Length: > 0 } family ? family : "unknown",
            BenchmarkEngine.String(device["comparability_class"]) is { Length: > 0 } comparabilityClass
                ? comparabilityClass
                : "unknown",
            profileId,
            BenchmarkEngine.String(result["captured_at"]),
            path,
            result);
    }
}

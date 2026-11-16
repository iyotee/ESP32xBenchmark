using System.Text.Json;

namespace Esp32Benchmark.Core;

public enum ThemePreference
{
    System,
    Dark,
    Light
}

public sealed record DesktopPreferences(
    ThemePreference Theme = ThemePreference.System,
    string CultureName = "");

public sealed class DesktopPreferencesStore(string? filePath = null)
{
    private readonly string _filePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ESP32xBenchmark", "preferences.json");

    public DesktopPreferences Load()
    {
        if (!File.Exists(_filePath))
        {
            return new DesktopPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_filePath))
                ?? throw new InvalidDataException("Desktop preferences file is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Desktop preferences file is invalid.", error);
        }
    }

    public void Save(DesktopPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidDataException("Desktop preferences path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

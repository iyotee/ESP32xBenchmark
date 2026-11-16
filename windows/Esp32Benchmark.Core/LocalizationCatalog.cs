using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Esp32Benchmark.Core;

public sealed record LanguageOption(string Name, string DisplayName, bool HasTranslation)
{
    public override string ToString() => DisplayName;
}

public sealed class LocalizationCatalog
{
    private const string ResourcePrefix = "Esp32Benchmark.Core.Localization.";
    private static readonly Assembly Assembly = typeof(LocalizationCatalog).Assembly;
    private static readonly IReadOnlyDictionary<string, string> English = ReadCatalog("en");
    private static readonly IReadOnlySet<string> CatalogLanguages = new HashSet<string>(
        ["en", "fr"], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LanguageOption> GetLanguageOptions(
        string windowsLabel, string englishFallbackLabel)
    {
        var languages = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(culture => culture.Name.Length > 0)
            .GroupBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(culture => culture.NativeName, StringComparer.CurrentCultureIgnoreCase)
            .Select(culture =>
            {
                var hasTranslation = CatalogLanguages.Contains(culture.TwoLetterISOLanguageName);
                var fallback = hasTranslation ? string.Empty : $" — {englishFallbackLabel}";
                return new LanguageOption(
                    culture.Name,
                    $"{culture.NativeName} ({culture.Name}){fallback}",
                    hasTranslation);
            })
            .ToList();
        languages.Insert(0, new LanguageOption(string.Empty, windowsLabel, true));
        return languages;
    }

    public IReadOnlyDictionary<string, string> GetStrings(CultureInfo culture)
    {
        var resolvedLanguage = CatalogLanguages.Contains(culture.TwoLetterISOLanguageName)
            ? culture.TwoLetterISOLanguageName
            : "en";
        if (resolvedLanguage == "en")
        {
            return English;
        }

        var localized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in English)
        {
            localized[key] = value;
        }

        foreach (var (key, value) in ReadCatalog(resolvedLanguage))
        {
            localized[key] = value;
        }

        return localized;
    }

    public string GetString(string key, CultureInfo culture)
    {
        var strings = GetStrings(culture);
        return strings.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"Localization key '{key}' is not defined.");
    }

    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        var resourceName = ResourcePrefix + language + ".json";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Missing localization catalog '{language}'.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Localization catalog '{language}' is empty.");
    }
}

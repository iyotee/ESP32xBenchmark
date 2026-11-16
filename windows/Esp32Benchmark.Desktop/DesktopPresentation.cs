using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Esp32Benchmark.Core;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Esp32Benchmark.Desktop;

public sealed class LocalizationService
{
    private readonly LocalizationCatalog _catalog = new();
    private readonly CultureInfo _windowsCulture = CultureInfo.CurrentUICulture;
    private ResourceDictionary? _activeDictionary;

    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;

    public event EventHandler? CultureChanged;

    public IReadOnlyList<LanguageOption> GetLanguageOptions() =>
        _catalog.GetLanguageOptions(
            Get("Text.LanguageWindows"), Get("Text.EnglishFallback"));

    public string Get(string key, params object?[] arguments)
    {
        var value = _catalog.GetString(key, Culture);
        return arguments.Length == 0
            ? value
            : string.Format(Culture, value, arguments);
    }
    public void Apply(string cultureName)
    {
        Culture = string.IsNullOrWhiteSpace(cultureName)
            ? _windowsCulture
            : CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;

        var dictionary = new ResourceDictionary();
        foreach (var (key, value) in _catalog.GetStrings(Culture))
        {
            dictionary[key] = value;
        }

        if (_activeDictionary is not null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(_activeDictionary);
        }

        Application.Current.Resources.MergedDictionaries.Add(dictionary);
        _activeDictionary = dictionary;
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class MonochromeThemeService : IDisposable
{
    private static readonly IReadOnlyDictionary<string, (string Dark, string Light)> Palette =
        new Dictionary<string, (string Dark, string Light)>(StringComparer.Ordinal)
        {
            ["AppBackground"] = ("#0B0B0D", "#F4F4F5"),
            ["SidebarBackground"] = ("#101012", "#EAEAEC"),
            ["HeaderBackground"] = ("#101012", "#FAFAFA"),
            ["PanelBackground"] = ("#171719", "#FFFFFF"),
            ["PanelAltBackground"] = ("#1E1E21", "#EFEFF1"),
            ["PanelBorder"] = ("#303034", "#D5D5D9"),
            ["PrimaryText"] = ("#F2F2F4", "#18181B"),
            ["SecondaryText"] = ("#B6B6BB", "#55555B"),
            ["MutedText"] = ("#85858C", "#717178"),
            ["AccentBrush"] = ("#F2F2F4", "#18181B"),
            ["AccentForeground"] = ("#111113", "#FFFFFF"),
            ["HoverBrush"] = ("#27272B", "#E5E5E8"),
            ["SelectedBrush"] = ("#323236", "#DADADD"),
            ["InputBackground"] = ("#1B1B1E", "#FFFFFF"),
            ["ConsoleBackground"] = ("#080809", "#18181A"),
            ["ConsoleForeground"] = ("#E4E4E7", "#F4F4F5"),
            ["GridHeaderBackground"] = ("#222225", "#E9E9EC"),
            ["GridLineBrush"] = ("#303034", "#D6D6D9"),
            ["StatusNeutralBrush"] = ("#89898F", "#66666C"),
            ["StatusWorkingBrush"] = ("#D2D2D5", "#3A3A3F"),
            ["StatusSuccessBrush"] = ("#E5E5E7", "#303035"),
            ["StatusErrorBrush"] = ("#A0A0A5", "#55555A"),
            ["DangerHoverBrush"] = ("#45454A", "#D0D0D4")
        };

    private ThemePreference _preference = ThemePreference.System;
    private bool _disposed;

    public event EventHandler? ThemeChanged;

    public MonochromeThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public ThemePreference Preference => _preference;
    public bool IsDark { get; private set; }

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        ApplyResolvedTheme();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _disposed = true;
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System ||
            Application.Current?.Dispatcher is not { } dispatcher ||
            dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyResolvedTheme);
    }

    private void ApplyResolvedTheme()
    {
        var dark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light
        };
        IsDark = dark;

        var applicationTheme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(
            applicationTheme,
            updateAccent: false);
        var accent = dark ? Color.FromRgb(242, 242, 244) : Color.FromRgb(24, 24, 27);
        ApplicationAccentColorManager.Apply(accent, applicationTheme, false, false);

        foreach (var (key, values) in Palette)
        {
            var color = ColorConverter.ConvertFromString(dark ? values.Dark : values.Light)
                ?? throw new InvalidDataException($"Invalid theme color for '{key}'.");
            Application.Current.Resources[key] = new SolidColorBrush((Color)color);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

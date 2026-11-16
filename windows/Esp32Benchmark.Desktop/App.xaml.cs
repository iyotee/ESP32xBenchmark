using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public partial class App : System.Windows.Application
{
    public DesktopPreferencesStore PreferencesStore { get; } = new();
    public LocalizationService Localization { get; } = new();
    public MonochromeThemeService ThemeService { get; } = new();
    public DesktopPreferences Preferences { get; private set; } = new();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Length > 0 &&
            string.Equals(e.Args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                PortableUpdateInstaller.ApplyAndRestart(e.Args);
                Shutdown(0);
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show(
                    $"The update could not be installed: {error.Message}",
                    "ESP32xBenchmark",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                Shutdown(1);
            }

            return;
        }

        if (e.Args.Contains("--verify-firmware-bundle", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                FirmwareProjectCache.VerifyEmbeddedProject();
                HistoryRepository.VerifyBundledProfiles();
                VerifyBrandingResources();
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }

            return;
        }

        if (e.Args.Length == 2 &&
            string.Equals(e.Args[0], "--cleanup-update-helper", StringComparison.OrdinalIgnoreCase))
        {
            PortableUpdateInstaller.ScheduleHelperCleanup(e.Args[1]);
        }

        Preferences = PreferencesStore.Load();
        Localization.Apply(Preferences.CultureName);
        ThemeService.Apply(Preferences.Theme);
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        ThemeService.Dispose();
        base.OnExit(e);
    }

    public void SavePreferences(DesktopPreferences preferences)
    {
        PreferencesStore.Save(preferences);
        Preferences = preferences;
    }

    private static void VerifyBrandingResources()
    {
        foreach (var resourceUri in new[]
                 {
                     "pack://application:,,,/Assets/logo-header-dark.png",
                     "pack://application:,,,/Assets/logo-header-light.png",
                     "pack://application:,,,/Assets/app-icon.ico"
                 })
        {
            var resource = GetResourceStream(new Uri(resourceUri, UriKind.Absolute))
                ?? throw new InvalidDataException($"Missing application resource '{resourceUri}'.");
            resource.Stream.Dispose();
        }
    }
}

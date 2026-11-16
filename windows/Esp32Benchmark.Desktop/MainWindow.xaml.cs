using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Esp32Benchmark.Core;
using System.Windows.Media.Animation;

namespace Esp32Benchmark.Desktop;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SerialDeviceService _serial = new();
    private readonly HistoryRepository _history = new();
    private readonly ObservableCollection<MetricRow> _metrics = [];
    private readonly ObservableCollection<CategoryRow> _categories = [];
    private readonly ObservableCollection<HistoryEntry> _runs = [];
    private readonly ObservableCollection<ComparisonRow> _comparisonRows = [];
    private readonly GitHubUpdateService _updateService = new();
    private readonly string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ESP32xBenchmark", "artifacts");
    private readonly DispatcherTimer _portRefreshTimer;
    private SerialConsoleSession? _console;
    private JsonObject? _lastResult;
    private IReadOnlyList<SerialPortDevice> _portDevices = [];
    private IReadOnlyList<string>? _knownPorts;
    private CancellationTokenSource? _autoDetectionCancellation;
    private string? _autoDetectionPort;
    private string? _queuedAutoDetectionPort;
    private Task? _autoDetectionTask;
    private UpdateReadyEventArgs? _pendingUpdate;
    private bool _busy;
    private bool _initialized;
    private bool _updatingPortChoices;
    private bool _updatingLanguageChoices;
    private bool _isClosing;
    private readonly App _app = (App)Application.Current;

    public MainWindow()
    {
        InitializeComponent();
        LanguageCombo.ItemsSource = _app.Localization.GetLanguageOptions();
        LanguageCombo.SelectedItem = LanguageCombo.Items
            .OfType<LanguageOption>()
            .FirstOrDefault(option => string.Equals(
                option.Name, _app.Preferences.CultureName, StringComparison.OrdinalIgnoreCase));
        UpdateThemeIcon();
        _app.Localization.CultureChanged += Localization_CultureChanged;
        _app.ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        _updateService.UpdateCheckFailed += UpdateService_UpdateCheckFailed;
        _updateService.UpdateReady += UpdateService_UpdateReady;
        UpdateBranding();
        MetricsGrid.ItemsSource = _metrics;
        CategoryGrid.ItemsSource = _categories;
        HistoryGrid.ItemsSource = _runs;
        ComparisonGrid.ItemsSource = _comparisonRows;
        ProjectTextBox.Text = string.Empty;
        ProjectTextBox.ToolTip = L("Text.UseBundledFirmware");
        _portRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _portRefreshTimer.Tick += (_, _) =>
        {
            RefreshPorts();
            TryInstallPendingUpdate();
        };
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            _app.Localization.CultureChanged -= Localization_CultureChanged;
            _app.ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
            _updateService.UpdateCheckFailed -= UpdateService_UpdateCheckFailed;
            _updateService.UpdateReady -= UpdateService_UpdateReady;
            _updateService.Dispose();
            _isClosing = true;
            _portRefreshTimer.Stop();
            _autoDetectionCancellation?.Cancel();
            _queuedAutoDetectionPort = null;
            DisconnectConsole();
        };
    }

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(
                HistoryGrid, e.OriginalSource as DependencyObject) is not DataGridRow
            {
                Item: HistoryEntry entry
            })
        {
            return;
        }

        ShowResult(entry.Result);
        ShowPage(OverviewPage);
        SetStatus(L("Text.HistoryOpened", entry.Target));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _initialized = true;
        RefreshPorts(force: true);
        _portRefreshTimer.Start();
        RefreshEnvironments();
        RefreshProfiles();
        RefreshHistory();
        _updateService.Start();
        FooterText.Text = string.Format(
            _app.Localization.Culture, L("Text.LocalHistory"), _history.HistoryDirectory);
        await Task.CompletedTask;
    }

    private void ShowOverview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private void ShowConsole_Click(object sender, RoutedEventArgs e) => ShowPage(ConsolePage);
    private void ShowHistory_Click(object sender, RoutedEventArgs e) => ShowPage(HistoryPage);

    private void ShowPage(UIElement page)
    {
        OverviewNavButton.Tag = ReferenceEquals(page, OverviewPage) ? "Active" : null;
        ConsoleNavButton.Tag = ReferenceEquals(page, ConsolePage) ? "Active" : null;
        HistoryNavButton.Tag = ReferenceEquals(page, HistoryPage) ? "Active" : null;
        OverviewPage.Visibility = Visibility.Collapsed;
        ConsolePage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            page.Opacity = 1;
            page.RenderTransform = Transform.Identity;
            return;
        }

        var slide = new TranslateTransform(0, 8);
        page.RenderTransform = slide;
        page.Opacity = 0;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var theme = _app.Preferences.Theme switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            _ => ThemePreference.System
        };
        try
        {
            var preferences = _app.Preferences with { Theme = theme };
            _app.SavePreferences(preferences);
            _app.ThemeService.Apply(theme);
            AnimateThemeIcon();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus(error.Message, error: true);
            UpdateThemeIcon();
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingLanguageChoices ||
            LanguageCombo.SelectedItem is not LanguageOption option)
        {
            return;
        }

        try
        {
            var preferences = _app.Preferences with { CultureName = option.Name };
            _app.SavePreferences(preferences);
            _app.Localization.Apply(option.Name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CultureNotFoundException)
        {
            SetStatus(error.Message, error: true);
        }
    }

    private void Localization_CultureChanged(object? sender, EventArgs e)
    {
        FlowDirection = _app.Localization.Culture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        _updatingLanguageChoices = true;
        try
        {
            LanguageCombo.ItemsSource = _app.Localization.GetLanguageOptions();
            LanguageCombo.SelectedItem = LanguageCombo.Items
                .OfType<LanguageOption>()
                .FirstOrDefault(option => string.Equals(
                    option.Name, _app.Preferences.CultureName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingLanguageChoices = false;
        }

        FooterText.Text = string.Format(
            _app.Localization.Culture, L("Text.LocalHistory"), _history.HistoryDirectory);
        UpdateThemeIcon();
        RefreshHistory();
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        UpdateBranding();
    }

    private void AnimateThemeIcon()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            UpdateThemeIcon();
            return;
        }

        ThemeToggleButton.IsEnabled = false;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        easing.Freeze();
        var rotateOut = new DoubleAnimation(0, 180, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = easing
        };
        rotateOut.Completed += (_, _) =>
        {
            UpdateThemeIcon();
            var rotateIn = new DoubleAnimation(180, 360, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = easing
            };
            rotateIn.Completed += (_, _) =>
            {
                ThemeIconRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                ThemeIconRotation.Angle = 0;
                ThemeToggleButton.IsEnabled = true;
            };
            ThemeIconRotation.BeginAnimation(RotateTransform.AngleProperty, rotateIn);
        };

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = easing
        };
        fadeOut.Completed += (_, _) =>
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = easing
            };
            fadeIn.Completed += (_, _) =>
            {
                ThemeIconPath.BeginAnimation(UIElement.OpacityProperty, null);
                ThemeIconPath.Opacity = 1;
            };
            ThemeIconPath.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };

        ThemeIconRotation.BeginAnimation(RotateTransform.AngleProperty, rotateOut);
        ThemeIconPath.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void UpdateThemeIcon()
    {
        var theme = _app.Preferences.Theme;
        ThemeIconPath.Data = Geometry.Parse(theme switch
        {
            ThemePreference.Dark =>
                "M 20.2,15.2 A 8.5,8.5 0 0 1 8.8,3.8 A 8.5,8.5 0 1 0 20.2,15.2 Z",
            ThemePreference.Light =>
                "M 12,7 A 5,5 0 1 0 12,17 A 5,5 0 1 0 12,7 M 12,1 L 12,3 M 12,21 L 12,23 M 4.22,4.22 L 5.64,5.64 M 18.36,18.36 L 19.78,19.78 M 1,12 L 3,12 M 21,12 L 23,12 M 4.22,19.78 L 5.64,18.36 M 18.36,5.64 L 19.78,4.22",
            _ => "M 3,4 L 21,4 L 21,16 L 3,16 Z M 12,16 L 12,20 M 8,20 L 16,20"
        });
        ThemeIconPath.Fill = theme == ThemePreference.Dark
            ? (Brush)FindResource("PrimaryText")
            : Brushes.Transparent;

        var themeName = theme switch
        {
            ThemePreference.Dark => "Text.ThemeDark",
            ThemePreference.Light => "Text.ThemeLight",
            _ => "Text.ThemeSystem"
        };
        var description = $"{L("Text.Theme")}: {L(themeName)}";
        ThemeToggleButton.ToolTip = description;
        AutomationProperties.SetName(ThemeToggleButton, description);
    }

    private void UpdateService_UpdateCheckFailed(object? sender, UpdateCheckFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                var message = L("Text.UpdateCheckFailed", e.Error.Message);
                if (_busy || _console is not null)
                {
                    FooterText.Text = message;
                }
                else
                {
                    SetStatus(message, error: true);
                }
            }
        }), DispatcherPriority.Background);
    }

    private void UpdateService_UpdateReady(object? sender, UpdateReadyEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing)
            {
                return;
            }

            _pendingUpdate = e;
            if (_busy || _console is not null)
            {
                FooterText.Text = L("Text.UpdatePending", e.VersionTag);
                return;
            }

            TryInstallPendingUpdate();
        }), DispatcherPriority.Send);
    }

    private void TryInstallPendingUpdate()
    {
        if (_isClosing || _busy || _console is not null || _pendingUpdate is not { } update)
        {
            return;
        }

        _pendingUpdate = null;
        try
        {
            PortableUpdateInstaller.Start(update.StagedExecutable, update.Sha256);
            SetStatus(L("Text.UpdateStarting", update.VersionTag));
            Close();
        }
        catch (Exception error) when (error is
                   IOException or UnauthorizedAccessException or NotSupportedException or
                   InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            SetStatus(error.Message, error: true);
        }
    }

    private void UpdateBranding()
    {
        var imageName = _app.ThemeService.IsDark
            ? "logo-header-dark.png"
            : "logo-header-light.png";
        var image = new BitmapImage(new Uri(
            $"pack://application:,,,/Assets/{imageName}", UriKind.Absolute));
        BrandLogo.Source = image;
        Icon = image;
    }

    private string L(string key, params object?[] arguments) =>
        _app.Localization.Get(key, arguments);

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = L(maximized ? "Text.Restore" : "Text.Maximize");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SetStatus(string message, bool working = false, bool error = false)
    {
        StatusText.Text = message;
        FooterText.Text = message;
        StatusIndicator.Fill = error
            ? (Brush)FindResource("StatusErrorBrush")
            : working
                ? (Brush)FindResource("StatusWorkingBrush")
                : (Brush)FindResource("StatusSuccessBrush");
    }

    private async Task RunBusyAsync(string caption, Func<Task> operation)
    {
        if (_busy)
        {
            MessageBox.Show(this, L("Text.OperationAlreadyRunning"),
                L("Text.PleaseWait"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busy = true;
        BusyIndicator.Visibility = Visibility.Visible;
        SetStatus(caption, working: true);
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            SetStatus(L("Text.OperationCancelled"));
        }
        catch (Exception error)
        {
            SetStatus(error.Message, error: true);
            MessageBox.Show(this, error.Message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            BusyIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPorts(force: true);
        RefreshEnvironments();
        if (PortCombo.SelectedItem is string port)
        {
            StartAutomaticBoardDetection(port, force: true);
        }
    }

    private void RefreshPorts(bool force = false)
    {
        var oldPort = PortCombo.SelectedItem as string;
        var oldConsolePort = ConsolePortCombo.SelectedItem as string;
        _portDevices = _serial.GetPortDevices();
        var ports = _portDevices.Select(device => device.PortName).ToArray();
        if (!force && _knownPorts is not null &&
            ports.SequenceEqual(_knownPorts, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _knownPorts = ports;
        var selectedPort = ports.Contains(oldPort) ? oldPort : ports.FirstOrDefault();
        _updatingPortChoices = true;
        try
        {
            PortCombo.ItemsSource = ports;
            ConsolePortCombo.ItemsSource = ports;
            SelectComboValue(PortCombo, selectedPort);
            SelectComboValue(ConsolePortCombo,
                ports.Contains(oldConsolePort) ? oldConsolePort : selectedPort);
        }
        finally
        {
            _updatingPortChoices = false;
        }

        if (ports.Length == 0)
        {
            _autoDetectionCancellation?.Cancel();
            _queuedAutoDetectionPort = null;
            SidebarBoardText.Text = L("Text.BoardNotDetected");
            SidebarClassText.Text = L("Text.ReconnectBoard");
            SetStatus(L("Text.NoPortsAutoRefresh"),
                error: true);
            return;
        }

        SidebarBoardText.Text = L("Text.BoardNotIdentified");
        SidebarClassText.Text = L("Text.DetectedPorts", string.Join(", ", ports));
        if (oldPort is null || !ports.Contains(oldPort))
        {
            SetStatus(L("Text.PortsFound", ports.Length));
        }

        if (!string.Equals(oldPort, selectedPort, StringComparison.OrdinalIgnoreCase) &&
            selectedPort is not null)
        {
            StartAutomaticBoardDetection(selectedPort);
        }
    }

    private void RefreshEnvironments()
    {
        var projectDirectory = string.IsNullOrWhiteSpace(ProjectTextBox.Text)
            ? FirmwareProjectCache.EnsureExtracted()
            : ProjectTextBox.Text.Trim();
        var configPath = Path.Combine(projectDirectory, "platformio.ini");
        var environments = new List<string>();
        if (File.Exists(configPath))
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var section = line.Trim();
                if (section.StartsWith("[env:", StringComparison.Ordinal) && section.EndsWith(']'))
                {
                    environments.Add(section[5..^1]);
                }
            }
        }

        if (environments.Count == 0)
        {
            environments.AddRange(
                ["esp32dev", "esp32s2", "esp32s3", "esp32c3", "esp32c3_supermini", "esp32c6", "esp8266"]);
        }

        var current = EnvironmentCombo.SelectedItem as string;
        EnvironmentCombo.ItemsSource = environments;
        SelectComboValue(EnvironmentCombo,
            environments.Contains(current, StringComparer.Ordinal) ? current : null);
    }

    private static void SelectComboValue(ComboBox comboBox, string? value)
    {
        if (value is null)
        {
            comboBox.SelectedIndex = -1;
            return;
        }

        comboBox.SelectedItem = value;
        if (comboBox.IsEditable)
        {
            comboBox.Text = value;
        }
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingPortChoices)
        {
            return;
        }

        var port = PortCombo.SelectedItem as string;
        if (port is null)
        {
            return;
        }

        SelectComboValue(ConsolePortCombo, port);
        SidebarBoardText.Text = L("Text.BoardNotIdentified");
        SidebarClassText.Text = L("Text.DetectingChip", port);
        StartAutomaticBoardDetection(port);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ProfileCombo.SelectedItem is not string profileId ||
            string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        try
        {
            var profile = _history.LoadProfile(profileId);
            SetStatus(L("Text.ReferenceSelected",
                profileId, BenchmarkEngine.String(profile["comparability_class"])));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            SetStatus(L("Text.ProfileLoadFailed", error.Message), error: true);
        }
    }

    private void IdentifyBoard_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSerialIdle())
        {
            return;
        }

        if (PortCombo.SelectedItem is not string port)
        {
            ShowNoPortMessage();
            return;
        }

        StartAutomaticBoardDetection(port, force: true);
    }

    private void StartAutomaticBoardDetection(string port, bool force = false)
    {
        if (_isClosing)
        {
            return;
        }

        if (!force && string.Equals(_autoDetectionPort, port, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_autoDetectionTask is { IsCompleted: false })
        {
            _queuedAutoDetectionPort = port;
            _autoDetectionCancellation?.Cancel();
            return;
        }

        if (_busy || _console is not null)
        {
            _queuedAutoDetectionPort = port;
            return;
        }

        _autoDetectionPort = port;
        var cancellation = new CancellationTokenSource();
        _autoDetectionCancellation = cancellation;
        _autoDetectionTask = DetectBoardAutomaticallyAsync(port, cancellation);
    }

    private async Task DetectBoardAutomaticallyAsync(
        string port, CancellationTokenSource cancellation)
    {
        _busy = true;
        BusyIndicator.Visibility = Visibility.Visible;
        SetStatus(L("Text.IdentifyingChip", port), working: true);
        try
        {
            JsonObject? identity = null;
            try
            {
                identity = await _serial.IdentifyAsync(
                    port, 115200, TimeSpan.FromSeconds(3), cancellation.Token);
            }
            catch (TimeoutException)
            {
                cancellation.Token.ThrowIfCancellationRequested();
            }

            if (identity is not null)
            {
                if (cancellation.IsCancellationRequested ||
                    !string.Equals(PortCombo.SelectedItem as string, port,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ApplyFirmwareIdentity(identity, port);
                return;
            }

            SetStatus(L("Text.ProbingRomBootloader", port),
                working: true);
            var detection = await PlatformIoRunner.DetectChipAsync(
                FirmwareProjectCache.EnsureExtracted(),
                port,
                new Progress<string>(line => SetStatus(line, working: true)),
                cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                !string.Equals(PortCombo.SelectedItem as string, port,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SidebarBoardText.Text = detection.Chip;
            SidebarClassText.Text = detection.Explanation;
            var environment = RecommendEnvironment(port, detection.Chip, detection.Environment);
            if (environment is not null && EnvironmentCombo.Items.Contains(environment))
            {
                SelectComboValue(EnvironmentCombo, environment);
                SetStatus(L("Text.DetectedChipTarget", detection.Chip, environment));
            }
            else
            {
                SetStatus(L("Text.DetectedChipNoTarget", detection.Chip),
                    error: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(L("Text.AutomaticIdentificationFailed", port, error.Message), error: true);
        }
        finally
        {
            _busy = false;
            BusyIndicator.Visibility = Visibility.Collapsed;
            _autoDetectionPort = null;
            if (ReferenceEquals(_autoDetectionCancellation, cancellation))
            {
                _autoDetectionCancellation = null;
            }

            cancellation.Dispose();
            _autoDetectionTask = null;
            var queuedPort = _queuedAutoDetectionPort;
            _queuedAutoDetectionPort = null;
            if (!_isClosing && queuedPort is not null &&
                string.Equals(PortCombo.SelectedItem as string, queuedPort,
                    StringComparison.OrdinalIgnoreCase))
            {
                StartAutomaticBoardDetection(queuedPort);
            }
        }
    }

    private void ApplyFirmwareIdentity(JsonObject identity, string port)
    {
        var (environment, explanation) = BoardEnvironmentRecommender.Recommend(identity);
        var target = BenchmarkEngine.String(identity["target"]);
        environment = RecommendEnvironment(port, target, environment);
        if (environment is not null && EnvironmentCombo.Items.Contains(environment))
        {
            SelectComboValue(EnvironmentCombo, environment);
        }

        var family = BenchmarkEngine.String(identity["family"]);
        var comparabilityClass = BenchmarkEngine.String(identity["comparability_class"]);
        SidebarBoardText.Text = target.Length == 0 ? L("Text.UnknownSupportedBoard") : target;
        SidebarClassText.Text = $"{family} · {comparabilityClass}\n{explanation}";
        SetStatus(environment is null
            ? L("Text.FirmwareIdentifiedNoTarget")
            : L("Text.BoardIdentifiedTargetSelected", target, environment));
    }

    private string? RecommendEnvironment(string port, string chip, string? environment)
    {
        var nativeEspressifUsb = _portDevices.Any(device =>
            string.Equals(device.PortName, port, StringComparison.OrdinalIgnoreCase) &&
            device.DeviceInstanceId?.Contains("VID_303A&PID_1001",
                StringComparison.OrdinalIgnoreCase) == true);
        if (nativeEspressifUsb && chip.Contains("ESP32-C3", StringComparison.OrdinalIgnoreCase) &&
            EnvironmentCombo.Items.Contains("esp32c3_supermini"))
        {
            return "esp32c3_supermini";
        }

        return environment;
    }

    private async void BuildFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSerialIdle() || !EnsureFirmwareEnvironmentSelected())
        {
            return;
        }

        var environment = EnvironmentCombo.Text.Trim();
        await RunBusyAsync(L("Text.BuildingFirmware"), async () =>
        {
            var progress = new Progress<string>(line => SetStatus(line, working: true));
            await PlatformIoRunner.BuildAsync(ResolveProjectDirectory(), environment, null, progress);
            SetStatus(L("Text.BuildCompleted", environment));
        });
    }

    private async void UploadCapture_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSerialIdle() || !EnsureFirmwareEnvironmentSelected())
        {
            return;
        }

        if (PortCombo.SelectedItem is not string port)
        {
            ShowNoPortMessage();
            return;
        }

        await RunBusyAsync(L("Text.UploadingAndCapturing"), () => CaptureAsync(upload: true, port));
    }

    private async void CaptureOnly_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSerialIdle())
        {
            return;
        }

        if (PortCombo.SelectedItem is not string port)
        {
            ShowNoPortMessage();
            return;
        }

        await RunBusyAsync(L("Text.CapturingBenchmark"), () => CaptureAsync(upload: false, port));
    }

    private async Task CaptureAsync(bool upload, string port)
    {
        if (!TryReadInt(BaudTextBox, L("Text.Baud"), out var baud) ||
            !TryReadInt(RunsTextBox, L("Text.Runs"), out var runCount) ||
            !TryReadInt(TimeoutTextBox, L("Text.Timeout"), out var timeoutSeconds))
        {
            SetStatus(L("Text.CaptureInvalidSetting"), error: true);
            return;
        }

        if (runCount is < 1 or > 20 || timeoutSeconds < 1 || baud < 1)
        {
            throw new InvalidDataException(L("Text.CaptureSettingsRange"));
        }

        if (upload)
        {
            var progress = new Progress<string>(line => SetStatus(line, working: true));
            await PlatformIoRunner.BuildAsync(
                ResolveProjectDirectory(),
                EnvironmentCombo.Text.Trim(),
                port,
                progress);
            SetStatus(L("Text.UploadedWaiting", EnvironmentCombo.Text.Trim(), port),
                working: true);
        }

        var result = await _serial.CaptureAsync(
            port, baud, runCount, TimeSpan.FromSeconds(timeoutSeconds));
        result["host"] = new JsonObject
        {
            ["collector_version"] = "ESP32xBenchmark .NET desktop",
            ["platform"] = Environment.OSVersion.ToString(),
            ["platformio_environment"] = EnvironmentCombo.Text.Trim(),
            ["serial_baud"] = baud
        };

        var profileId = ProfileCombo.SelectedItem as string ?? HistoryRepository.DefaultProfileId;
        try
        {
            result["scores"] = BenchmarkEngine.Score(
                result, _history.LoadProfile(profileId), allowCrossClass: true);
        }
        catch (InvalidDataException error)
        {
            MessageBox.Show(this, L("Text.CaptureScoringFailed", error.Message),
                L("Text.ScoringUnavailable"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ReportWriter.WriteResult(result, Path.Combine(_outputDirectory, "latest"));
        _history.SaveRun(result, profileId ?? "unscored");
        ShowResult(result);
        RefreshHistory();
        SetStatus(L("Text.CaptureCompleted", runCount, port));
    }

    private bool EnsureSerialIdle()
    {
        if (_busy)
        {
            MessageBox.Show(this, L("Text.WaitForOperation"),
                L("Text.PleaseWait"), MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_console is not null)
        {
            MessageBox.Show(this,
                L("Text.DisconnectConsoleFirst"),
                L("Text.ComPortInUse"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void ShowNoPortMessage() =>
        MessageBox.Show(this,
            L("Text.NoPortSelected"),
            L("Text.NoSerialPort"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool EnsureFirmwareEnvironmentSelected()
    {
        if (EnvironmentCombo.SelectedItem is string environment &&
            !string.IsNullOrWhiteSpace(environment))
        {
            return true;
        }

        MessageBox.Show(this,
            L("Text.SelectMatchingTarget"),
            L("Text.SelectBoardType"), MessageBoxButton.OK, MessageBoxImage.Warning);
        EnvironmentCombo.Focus();
        return false;
    }

    private bool TryReadInt(TextBox textBox, string label, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        MessageBox.Show(L("Text.InvalidInteger", label), L("Text.InvalidSetting"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        textBox.Focus();
        textBox.SelectAll();
        return false;
    }

    private void ShowResult(JsonObject result)
    {
        _lastResult = result;
        var device = result["device"] as JsonObject;
        var target = BenchmarkEngine.String(device?["target"]);
        var family = BenchmarkEngine.String(device?["family"]);
        var comparabilityClass = BenchmarkEngine.String(device?["comparability_class"]);
        SidebarBoardText.Text = target.Length == 0 ? L("Text.UnknownBoard") : target;
        SidebarClassText.Text = $"{family} · {comparabilityClass}";
        _metrics.Clear();
        _categories.Clear();

        var scores = result["scores"] as JsonObject;
        var scoreText = FormatNumber(scores?["global_score"]);
        GlobalScoreText.Text = scoreText;
        CoverageText.Text = scores is null ? "—" : FormatPercent(scores["coverage"]);
        ScoreProfileText.Text = scores is null
            ? L("Text.EmptyScore")
            : L("Text.ReferenceProfileValue", BenchmarkEngine.String(scores["reference_profile"])) +
              (scores["cross_class"]?.GetValue<bool>() == true
                  ? L("Text.CrossClassMatchingWorkloads")
                  : string.Empty);

        var categories = scores?["categories"] as JsonObject;
        var scoredMetrics = new Dictionary<string, string>(StringComparer.Ordinal);
        if (categories is not null)
        {
            foreach (var (category, node) in categories)
            {
                var categoryScore = node as JsonObject;
                _categories.Add(new CategoryRow(
                    category,
                    FormatNumber(categoryScore?["score"]),
                    FormatPercent(categoryScore?["weight"])));
                if (categoryScore?["metrics"] is JsonArray items)
                {
                    foreach (var metric in items.OfType<JsonObject>())
                    {
                        scoredMetrics[BenchmarkEngine.String(metric["name"])] =
                            FormatNumber(metric["value"]);
                    }
                }
            }
        }

        AvailableCategoriesText.Text = categories is null || categories.Count == 0
            ? L("Text.NoCategory")
            : string.Join(", ", categories.Select(item => item.Key));

        if (result["metrics"] is JsonArray metrics)
        {
            foreach (var metric in metrics.OfType<JsonObject>())
            {
                var name = BenchmarkEngine.String(metric["name"]);
                var status = BenchmarkEngine.String(metric["status"]);
                if (scoredMetrics.TryGetValue(name, out var index))
                {
                    status += $" · index {index}";
                }

                _metrics.Add(new MetricRow(
                    BenchmarkEngine.String(metric["category"]),
                    name,
                    FormatNumber(metric["value"]),
                    BenchmarkEngine.String(metric["unit"]),
                    status,
                    metric["stdev_pct"] is null ? "—" : FormatNumber(metric["stdev_pct"]) + "%",
                    BenchmarkEngine.String(metric["reason"])));
            }
        }
    }

    private void ConsoleConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (ConsolePortCombo.SelectedItem is not string port)
        {
            ShowNoPortMessage();
            return;
        }

        if (!TryReadInt(ConsoleBaudTextBox, L("Text.Baud"), out var baud))
        {
            return;
        }

        try
        {
            _console = _serial.OpenConsole(port, baud);
            _console.TextReceived += ConsoleTextReceived;
            _console.ReaderFailed += ConsoleReaderFailed;
            ConsoleConnectButton.Content = L("Text.Disconnect");
            ConsoleConnectButton.Click -= ConsoleConnect_Click;
            ConsoleConnectButton.Click += ConsoleDisconnect_Click;
            SetStatus(L("Text.ConsoleConnected", port, baud));
            AppendConsole(L("Text.ConsoleConnectedNotice", port));
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, L("Text.SerialConnectionFailed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConsoleDisconnect_Click(object sender, RoutedEventArgs e) => DisconnectConsole();

    private void DisconnectConsole()
    {
        if (_console is null)
        {
            return;
        }

        _console.TextReceived -= ConsoleTextReceived;
        _console.ReaderFailed -= ConsoleReaderFailed;
        _console.Dispose();
        _console = null;
        ConsoleConnectButton.Content = L("Text.Connect");
        ConsoleConnectButton.Click -= ConsoleDisconnect_Click;
        ConsoleConnectButton.Click += ConsoleConnect_Click;
        AppendConsole(L("Text.ConsoleDisconnectedNotice"));
        SetStatus(L("Text.ConsoleDisconnected"));
    }

    private void ConsoleTextReceived(string text) =>
        Dispatcher.BeginInvoke(new Action(() => AppendConsole(text)));

    private void ConsoleReaderFailed(Exception error) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendConsole(L("Text.SerialErrorNotice", error.Message));
            SetStatus(L("Text.SerialError", error.Message), error: true);
        }));

    private void AppendConsole(string text)
    {
        ConsoleTextBox.AppendText(text.TrimEnd('\r', '\n') + Environment.NewLine);
        if (ConsoleTextBox.Text.Length > 120_000)
        {
            ConsoleTextBox.Select(0, 40_000);
            ConsoleTextBox.SelectedText = "";
        }

        ConsoleTextBox.ScrollToEnd();
    }

    private void ConsoleSend_Click(object sender, RoutedEventArgs e) => SendConsoleLine();

    private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SendConsoleLine();
            e.Handled = true;
        }
    }

    private void SendConsoleLine()
    {
        if (_console is null)
        {
            MessageBox.Show(this, L("Text.ConnectConsoleBeforeSending"),
                L("Text.ConsoleDisconnected"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var line = ConsoleInputTextBox.Text;
            _console.SendLine(line);
            AppendConsole($"> {line}");
            ConsoleInputTextBox.Clear();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, L("Text.SerialWriteFailed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("Text.SelectPlatformIoProject"),
            Filter = L("Text.PlatformIoConfigFilter"),
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(ProjectTextBox.Text)
                ? ProjectTextBox.Text
                : Environment.CurrentDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            ProjectTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            RefreshEnvironments();
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_outputDirectory);
        Process.Start(new ProcessStartInfo(_outputDirectory) { UseShellExecute = true });
    }

    private void RefreshProfiles()
    {
        var selected = ProfileCombo.SelectedItem as string;
        var profiles = _history.GetProfileIds();
        ProfileCombo.ItemsSource = profiles;
        SelectComboValue(ProfileCombo, selected is not null && profiles.Contains(selected)
            ? selected
            : profiles.Contains(HistoryRepository.DefaultProfileId)
                ? HistoryRepository.DefaultProfileId
                : profiles.FirstOrDefault());
    }

    private void RefreshHistory()
    {
        _runs.Clear();
        foreach (var entry in _history.GetRuns())
        {
            _runs.Add(entry);
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("Text.ImportMeasuredProfile"),
            Filter = L("Text.ReferenceProfileFilter"),
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "samples")
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var profileId = _history.ImportProfile(dialog.FileName);
            RefreshProfiles();
            SelectComboValue(ProfileCombo, profileId);
            SetStatus(L("Text.ProfileImported", profileId));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, error.Message, L("Text.CouldNotImportProfile"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportResult_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("Text.ImportBenchmarkResults"),
            Filter = L("Text.BenchmarkJsonFilter")
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(dialog.FileName)) is not JsonObject result ||
                result["device"] is not JsonObject ||
                result["metrics"] is not JsonArray)
            {
                throw new InvalidDataException(L("Text.InvalidBenchmarkResult"));
            }

            var profileId = ProfileCombo.SelectedItem as string ?? HistoryRepository.DefaultProfileId;
            try
            {
                result["scores"] = BenchmarkEngine.Score(
                    result, _history.LoadProfile(profileId), allowCrossClass: true);
            }
            catch (InvalidDataException error)
            {
                MessageBox.Show(this, L("Text.ImportScoringFailed", error.Message),
                    L("Text.ScoringUnavailable"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ReportWriter.WriteResult(result, Path.Combine(_outputDirectory, "imported"));
            _history.SaveRun(result, profileId ?? "unscored");
            ShowResult(result);
            RefreshHistory();
            SetStatus(L("Text.BenchmarkImported"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, error.Message, L("Text.CouldNotImportResults"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CalibrateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItems.Count != 1 ||
            HistoryGrid.SelectedItem is not HistoryEntry selected)
        {
            MessageBox.Show(this, L("Text.SelectOneRun"),
                L("Text.SelectOneRunTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var suggested = $"{selected.ComparabilityClass}-baseline";
        var profileId = AskForProfileId(suggested);
        if (profileId is null)
        {
            return;
        }

        try
        {
            var profile = BenchmarkEngine.MakeReferenceProfile(selected.Result, profileId);
            var profilePath = _history.SaveProfile(profile);
            RefreshProfiles();
            SelectComboValue(ProfileCombo, profileId);
            SetStatus(L("Text.CalibrationProfileSaved", profilePath));
            MessageBox.Show(this,
                L("Text.CalibrationCompleteMessage", profileId, profilePath),
                L("Text.CalibrationComplete"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, L("Text.CouldNotCreateProfile"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? AskForProfileId(string initialValue)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = L("Text.CreateMeasuredProfile"),
            Width = 470,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("PanelBackground"),
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText")
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = L("Text.ProfileId"),
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryText"),
            Margin = new Thickness(0, 0, 0, 8)
        });
        var input = new TextBox { Text = initialValue, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(input);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = L("Text.Cancel"), Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = new Button { Content = L("Text.CreateProfile"), Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(dialog, L("Text.EnterProfileId"), L("Text.ProfileId"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<HistoryEntry>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, L("Text.SelectOneOrMoreRuns"),
                L("Text.NothingSelected"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, L("Text.ConfirmDeleteRuns", selected.Length),
                L("Text.ConfirmDeletion"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var entry in selected)
        {
            _history.DeleteRun(entry);
        }

        RefreshHistory();
        SetStatus(L("Text.DeletedRuns", selected.Length));
    }

    private async void CompareSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<HistoryEntry>().ToArray();
        if (selected.Length < 2)
        {
            MessageBox.Show(this, L("Text.SelectAtLeastTwoRuns"),
                L("Text.SelectRuns"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ProfileCombo.SelectedItem is not string profileId)
        {
            MessageBox.Show(this, L("Text.SelectReferenceFirst"),
                L("Text.SelectReference"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync(L("Text.ComparingBoardRuns"), async () =>
        {
            var results = selected.Select(entry => entry.Result).ToArray();
            var comparison = BenchmarkEngine.Compare(results, _history.LoadProfile(profileId));
            var comparisonDirectory = Path.Combine(_outputDirectory, "comparison");
            ReportWriter.WriteComparison(comparison, comparisonDirectory);
            ShowComparison(comparison);
            SetStatus(L("Text.ComparisonCompleted", results.Length,
                comparison["common_metrics"]!.AsArray().Count, comparisonDirectory));
            await Task.CompletedTask;
        });
    }

    private void ShowComparison(JsonObject comparison)
    {
        _comparisonRows.Clear();
        if (comparison["results"] is JsonArray results)
        {
            foreach (var result in results.OfType<JsonObject>())
            {
                _comparisonRows.Add(new ComparisonRow(
                    BenchmarkEngine.String(result["target"]),
                    BenchmarkEngine.String(result["comparability_class"]),
                    FormatNumber(result["global_score"]),
                    FormatPercent(result["coverage"])));
            }
        }

        var metricCount = (comparison["common_metrics"] as JsonArray)?.Count ?? 0;
        ComparisonSummaryText.Text =
            L("Text.ComparisonSummary", BenchmarkEngine.String(comparison["reference_profile"]),
                metricCount, FormatPercent(comparison["coverage"]));
        ShowPage(HistoryPage);
    }

    private static string FormatNumber(JsonNode? value) =>
        BenchmarkEngine.TryNumber(value, out var number)
            ? number.ToString("0.##", CultureInfo.CurrentCulture)
            : "—";

    private static string FormatPercent(JsonNode? value) =>
        BenchmarkEngine.TryNumber(value, out var number)
            ? (number * 100).ToString("0", CultureInfo.CurrentCulture) + "%"
            : "—";

    private string ResolveProjectDirectory()
    {
        if (string.IsNullOrWhiteSpace(ProjectTextBox.Text))
        {
            return FirmwareProjectCache.EnsureExtracted();
        }

        var projectDirectory = Path.GetFullPath(ProjectTextBox.Text.Trim());
        if (!File.Exists(Path.Combine(projectDirectory, "platformio.ini")))
        {
            throw new FileNotFoundException(L("Text.PlatformIoConfigNotFound", projectDirectory));
        }

        return projectDirectory;
    }

    private sealed record MetricRow(
        string Category, string Name, string Value, string Unit, string Status, string Variation, string Reason);
    private sealed record CategoryRow(string Category, string Score, string Weight);
    private sealed record ComparisonRow(string Target, string ComparabilityClass, string GlobalScore, string Coverage);
}

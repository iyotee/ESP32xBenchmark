using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SerialDeviceService _serial = new();
    private readonly HistoryRepository _history = new();
    private readonly ObservableCollection<MetricRow> _metrics = [];
    private readonly ObservableCollection<CategoryRow> _categories = [];
    private readonly ObservableCollection<HistoryEntry> _runs = [];
    private readonly ObservableCollection<ComparisonRow> _comparisonRows = [];
    private readonly string _outputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ESP32xBenchmark", "artifacts");
    private SerialConsoleSession? _console;
    private JsonObject? _lastResult;
    private bool _busy;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        MetricsGrid.ItemsSource = _metrics;
        CategoryGrid.ItemsSource = _categories;
        HistoryGrid.ItemsSource = _runs;
        ComparisonGrid.ItemsSource = _comparisonRows;
        ProjectTextBox.Text = string.Empty;
        ProjectTextBox.ToolTip = "Leave blank to build from the firmware sources bundled with this app.";
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => DisconnectConsole();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _initialized = true;
        RefreshPorts();
        RefreshEnvironments();
        RefreshProfiles();
        RefreshHistory();
        FooterText.Text = $"Local history: {_history.HistoryDirectory}";
        await Task.CompletedTask;
    }

    private void ShowOverview_Click(object sender, RoutedEventArgs e) => ShowPage(OverviewPage);
    private void ShowConsole_Click(object sender, RoutedEventArgs e) => ShowPage(ConsolePage);
    private void ShowHistory_Click(object sender, RoutedEventArgs e) => ShowPage(HistoryPage);

    private void ShowPage(UIElement page)
    {
        OverviewPage.Visibility = Visibility.Collapsed;
        ConsolePage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void SetStatus(string message, bool working = false, bool error = false)
    {
        StatusText.Text = message;
        FooterText.Text = message;
        StatusIndicator.Fill = error
            ? System.Windows.Media.Brushes.IndianRed
            : working
                ? System.Windows.Media.Brushes.Gold
                : System.Windows.Media.Brushes.LightGreen;
    }

    private async Task RunBusyAsync(string caption, Func<Task> operation)
    {
        if (_busy)
        {
            MessageBox.Show(this, "A build or capture task is already running.",
                "Please wait", MessageBoxButton.OK, MessageBoxImage.Information);
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
            SetStatus("Operation cancelled");
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
        RefreshPorts();
        RefreshEnvironments();
    }

    private void RefreshPorts()
    {
        var oldPort = PortCombo.SelectedItem as string;
        var oldConsolePort = ConsolePortCombo.SelectedItem as string;
        var ports = _serial.GetPorts();
        PortCombo.ItemsSource = ports;
        ConsolePortCombo.ItemsSource = ports;
        SelectComboValue(PortCombo, ports.Contains(oldPort) ? oldPort : ports.FirstOrDefault());
        SelectComboValue(ConsolePortCombo,
            ports.Contains(oldConsolePort) ? oldConsolePort : ports.FirstOrDefault());

        if (ports.Count == 0)
        {
            SidebarBoardText.Text = "No COM ports found";
            SidebarClassText.Text = "Connect a supported board and refresh ports.";
            SetStatus("No serial COM ports found — connect a board and choose Refresh");
            return;
        }

        if (oldPort is null || !ports.Contains(oldPort))
        {
            SidebarBoardText.Text = "Board not identified";
            SidebarClassText.Text = $"Detected ports: {string.Join(", ", ports)}";
            SetStatus($"Found {ports.Count} serial port(s)");
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

        var current = EnvironmentCombo.Text;
        EnvironmentCombo.ItemsSource = environments;
        SelectComboValue(EnvironmentCombo,
            environments.Contains(current, StringComparer.Ordinal) ? current : environments[0]);
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
        if (!_initialized)
        {
            return;
        }

        var port = PortCombo.SelectedItem as string;
        if (port is null)
        {
            return;
        }

        SelectComboValue(ConsolePortCombo, port);
        SidebarBoardText.Text = "Board not identified";
        SidebarClassText.Text = $"Selected port: {port}. Identify benchmark firmware to recommend a target.";
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
            SetStatus($"Reference: {profileId} · {BenchmarkEngine.String(profile["comparability_class"])}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            SetStatus($"Could not load profile: {error.Message}", error: true);
        }
    }

    private async void IdentifyBoard_Click(object sender, RoutedEventArgs e)
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

        if (!TryReadInt(BaudTextBox, "baud rate", out var baud))
        {
            return;
        }

        await RunBusyAsync("Identifying board", async () =>
        {
            var identity = await _serial.IdentifyAsync(port, baud, TimeSpan.FromSeconds(7));
            var (environment, explanation) = BoardEnvironmentRecommender.Recommend(identity);
            if (environment is not null && EnvironmentCombo.Items.Contains(environment))
            {
                SelectComboValue(EnvironmentCombo, environment);
            }

            var target = BenchmarkEngine.String(identity["target"]);
            var family = BenchmarkEngine.String(identity["family"]);
            var comparabilityClass = BenchmarkEngine.String(identity["comparability_class"]);
            SidebarBoardText.Text = target.Length == 0 ? "Unknown supported board" : target;
            SidebarClassText.Text = $"{family} · {comparabilityClass}\n{explanation}";
            SetStatus(environment is null
                ? "Firmware identity received; choose a supported environment manually"
                : $"Identified {target} · recommended {environment}");
        });
    }

    private async void BuildFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSerialIdle())
        {
            return;
        }

        var environment = EnvironmentCombo.Text.Trim();
        await RunBusyAsync("Building firmware", async () =>
        {
            var progress = new Progress<string>(line => SetStatus(line, working: true));
            await PlatformIoRunner.BuildAsync(ResolveProjectDirectory(), environment, null, progress);
            SetStatus($"Build completed · {environment}");
        });
    }

    private async void UploadCapture_Click(object sender, RoutedEventArgs e)
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

        await RunBusyAsync("Uploading firmware and capturing", () => CaptureAsync(upload: true, port));
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

        await RunBusyAsync("Capturing benchmark", () => CaptureAsync(upload: false, port));
    }

    private async Task CaptureAsync(bool upload, string port)
    {
        if (!TryReadInt(BaudTextBox, "baud rate", out var baud) ||
            !TryReadInt(RunsTextBox, "run count", out var runCount) ||
            !TryReadInt(TimeoutTextBox, "capture timeout", out var timeoutSeconds))
        {
            SetStatus("Capture cancelled because a setting is invalid", error: true);
            return;
        }

        if (runCount is < 1 or > 20 || timeoutSeconds < 1 || baud < 1)
        {
            throw new InvalidDataException("Use a positive baud rate/timeout and a run count from 1 to 20.");
        }

        if (upload)
        {
            var progress = new Progress<string>(line => SetStatus(line, working: true));
            await PlatformIoRunner.BuildAsync(
                ResolveProjectDirectory(),
                EnvironmentCombo.Text.Trim(),
                port,
                progress);
            SetStatus($"Uploaded {EnvironmentCombo.Text.Trim()} to {port}; waiting for benchmark runs",
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

        var profileId = ProfileCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(profileId))
        {
            try
            {
                result["scores"] = BenchmarkEngine.Score(result, _history.LoadProfile(profileId));
            }
            catch (InvalidDataException error)
            {
                MessageBox.Show(this,
                    $"The capture completed, but this profile cannot score the board:\n\n{error.Message}\n\n" +
                    "The raw measurements will still be saved as history.",
                    "Reference class mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        ReportWriter.WriteResult(result, Path.Combine(_outputDirectory, "latest"));
        _history.SaveRun(result, profileId ?? "unscored");
        ShowResult(result);
        RefreshHistory();
        SetStatus($"Captured {runCount} complete run(s) from {port} · reports saved");
    }

    private bool EnsureSerialIdle()
    {
        if (_busy)
        {
            MessageBox.Show(this, "Wait for the current operation to finish.",
                "Please wait", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (_console is not null)
        {
            MessageBox.Show(this,
                "Disconnect the interactive serial console before identifying, building, uploading, or capturing.",
                "COM port is in use", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void ShowNoPortMessage() =>
        MessageBox.Show(this,
            "No COM port is selected. Connect a board, refresh the port list, and select its COM port.",
            "No serial port", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool TryReadInt(TextBox textBox, string label, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        MessageBox.Show($"Enter a valid integer for the {label}.", "Invalid setting",
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
        SidebarBoardText.Text = target.Length == 0 ? "Unknown board" : target;
        SidebarClassText.Text = $"{family} · {comparabilityClass}";
        _metrics.Clear();
        _categories.Clear();

        var scores = result["scores"] as JsonObject;
        var scoreText = FormatNumber(scores?["global_score"]);
        GlobalScoreText.Text = scoreText;
        CoverageText.Text = scores is null ? "—" : FormatPercent(scores["coverage"]);
        ScoreProfileText.Text = scores is null
            ? "Unscored · select an applicable reference profile"
            : $"Reference: {BenchmarkEngine.String(scores["reference_profile"])}";

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
            ? "None"
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

        if (!TryReadInt(ConsoleBaudTextBox, "baud rate", out var baud))
        {
            return;
        }

        try
        {
            _console = _serial.OpenConsole(port, baud);
            _console.TextReceived += ConsoleTextReceived;
            _console.ReaderFailed += ConsoleReaderFailed;
            ConsoleConnectButton.Content = "Disconnect";
            ConsoleConnectButton.Click -= ConsoleConnect_Click;
            ConsoleConnectButton.Click += ConsoleDisconnect_Click;
            SetStatus($"Serial console connected · {port} @ {baud}");
            AppendConsole($"[Connected to {port}; opening the port may reset the board.]");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Serial connection failed",
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
        ConsoleConnectButton.Content = "Connect";
        ConsoleConnectButton.Click -= ConsoleDisconnect_Click;
        ConsoleConnectButton.Click += ConsoleConnect_Click;
        AppendConsole("[Disconnected]");
        SetStatus("Serial console disconnected");
    }

    private void ConsoleTextReceived(string text) =>
        Dispatcher.BeginInvoke(new Action(() => AppendConsole(text)));

    private void ConsoleReaderFailed(Exception error) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendConsole($"[Serial error: {error.Message}]");
            SetStatus($"Serial error · {error.Message}", error: true);
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
            MessageBox.Show(this, "Connect the serial console before sending a command.",
                "Console disconnected", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, error.Message, "Serial write failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the PlatformIO project configuration",
            Filter = "PlatformIO configuration (platformio.ini)|platformio.ini",
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
            Title = "Import a measured reference profile",
            Filter = "Reference profile JSON (*.json)|*.json|All files (*.*)|*.*",
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
            SetStatus($"Imported measured reference profile · {profileId}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, error.Message, "Could not import profile",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportResult_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a benchmark results.json",
            Filter = "Benchmark JSON (*.json)|*.json|All files (*.*)|*.*"
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
                throw new InvalidDataException("The selected JSON is not a benchmark result.");
            }

            var profileId = ProfileCombo.SelectedItem as string;
            if (profileId is not null)
            {
                try
                {
                    result["scores"] = BenchmarkEngine.Score(result, _history.LoadProfile(profileId));
                }
                catch (InvalidDataException error)
                {
                    MessageBox.Show(this, $"Result imported without scoring:\n\n{error.Message}",
                        "Reference mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            ReportWriter.WriteResult(result, Path.Combine(_outputDirectory, "imported"));
            _history.SaveRun(result, profileId ?? "unscored");
            ShowResult(result);
            RefreshHistory();
            SetStatus("Imported benchmark result to local history");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            MessageBox.Show(this, error.Message, "Could not import results",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CalibrateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItems.Count != 1 ||
            HistoryGrid.SelectedItem is not HistoryEntry selected)
        {
            MessageBox.Show(this, "Select exactly one measured run to create a reference profile.",
                "Select one run", MessageBoxButton.OK, MessageBoxImage.Information);
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
            SetStatus($"Calibration profile saved · {profilePath}");
            MessageBox.Show(this,
                $"Measured reference profile '{profileId}' was created.\n\n{profilePath}\n\n" +
                "This baseline is only valid for its declared comparability class.",
                "Calibration complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not create calibration profile",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? AskForProfileId(string initialValue)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Create measured reference profile",
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
            Text = "Profile ID",
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
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = new Button { Content = "Create profile", Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(dialog, "Enter a profile ID.", "Profile ID",
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
            MessageBox.Show(this, "Select one or more local history entries to delete.",
                "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Delete {selected.Length} selected history entry(s)?",
                "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var entry in selected)
        {
            _history.DeleteRun(entry);
        }

        RefreshHistory();
        SetStatus($"Deleted {selected.Length} history entry(s)");
    }

    private async void CompareSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.Cast<HistoryEntry>().ToArray();
        if (selected.Length < 2)
        {
            MessageBox.Show(this, "Select at least two benchmark runs to compare.",
                "Select runs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ProfileCombo.SelectedItem is not string profileId)
        {
            MessageBox.Show(this, "Import or select one measured shared reference profile first.",
                "Select reference", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync("Comparing board runs", async () =>
        {
            var results = selected.Select(entry => entry.Result).ToArray();
            var comparison = BenchmarkEngine.Compare(results, _history.LoadProfile(profileId));
            var comparisonDirectory = Path.Combine(_outputDirectory, "comparison");
            ReportWriter.WriteComparison(comparison, comparisonDirectory);
            ShowComparison(comparison);
            SetStatus(
                $"Compared {results.Length} boards · {comparison["common_metrics"]!.AsArray().Count} shared stable metric(s) · " +
                $"reports saved to {comparisonDirectory}");
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
            $"{BenchmarkEngine.String(comparison["reference_profile"])} · " +
            $"{metricCount} stable metrics · {FormatPercent(comparison["coverage"])} common coverage";
        ShowPage(HistoryPage);
    }

    private static string FormatNumber(JsonNode? value) =>
        BenchmarkEngine.TryNumber(value, out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatPercent(JsonNode? value) =>
        BenchmarkEngine.TryNumber(value, out var number)
            ? (number * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
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
            throw new FileNotFoundException(
                $"No platformio.ini was found in {projectDirectory}. " +
                "Choose a PlatformIO project folder or leave the field blank to use the bundled firmware.");
        }

        return projectDirectory;
    }

    private sealed record MetricRow(
        string Category, string Name, string Value, string Unit, string Status, string Variation, string Reason);
    private sealed record CategoryRow(string Category, string Score, string Weight);
    private sealed record ComparisonRow(string Target, string ComparabilityClass, string GlobalScore, string Coverage);
}

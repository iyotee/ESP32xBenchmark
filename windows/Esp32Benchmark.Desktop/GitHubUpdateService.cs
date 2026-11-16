using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public sealed class UpdateCheckFailedEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}

public sealed class UpdateReadyEventArgs(
    string versionTag,
    string stagedExecutable,
    string sha256) : EventArgs
{
    public string VersionTag { get; } = versionTag;
    public string StagedExecutable { get; } = stagedExecutable;
    public string Sha256 { get; } = sha256;
}

public sealed class GitHubUpdateService : IDisposable
{
    private const string AssetName = "ESP32xBenchmark-win-x64.exe";
    private static readonly Uri ManifestUri = new(
        "https://raw.githubusercontent.com/iyotee/ESP32xBenchmark/main/latest.json");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private const long MaximumAssetSize = 250L * 1024 * 1024;
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DatedReleaseVersion _currentVersion;
    private EntityTagHeaderValue? _manifestETag;
    private Task? _monitorTask;
    private string? _lastError;
    private int _started;
    private int _updateStarted;

    public GitHubUpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ESP32xBenchmark-Updater/1.0");
        var assemblyVersion = typeof(App).Assembly.GetName().Version
            ?? throw new InvalidDataException("The application assembly has no version.");
        var currentTag = string.Format(
            CultureInfo.InvariantCulture,
            "V{0}.{1:00}.{2:00}.{3}",
            assemblyVersion.Major,
            assemblyVersion.Minor,
            assemblyVersion.Build,
            Math.Max(assemblyVersion.Revision, 1));
        if (!DatedReleaseVersion.TryParseTag(currentTag, out _currentVersion))
        {
            throw new InvalidDataException($"Application version '{currentTag}' is not a dated release version.");
        }
    }

    public event EventHandler<UpdateCheckFailedEventArgs>? UpdateCheckFailed;
    public event EventHandler<UpdateReadyEventArgs>? UpdateReady;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _monitorTask = MonitorAsync(_cancellation.Token);
        _ = _monitorTask.ContinueWith(
            task => Trace.TraceError($"The update monitor stopped unexpectedly: {task.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdatesAsync(cancellationToken);
                _lastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is
                       HttpRequestException or OperationCanceledException or JsonException or
                       InvalidDataException or IOException or UnauthorizedAccessException or
                       NotSupportedException or InvalidOperationException or ArgumentException or
                       System.ComponentModel.Win32Exception)
            {
                if (!string.Equals(_lastError, error.Message, StringComparison.Ordinal))
                {
                    _lastError = error.Message;
                    UpdateCheckFailed?.Invoke(this, new UpdateCheckFailedEventArgs(error));
                }
            }

            try
            {
                await Task.Delay(CheckInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (_manifestETag is not null)
        {
            request.Headers.IfNoneMatch.Add(_manifestETag);
        }

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _manifestETag = null;
            return;
        }

        response.EnsureSuccessStatusCode();
        await using var manifestStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<LatestReleaseManifest>(
            manifestStream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The GitHub update manifest is empty.");
        var latestVersion = ValidateManifest(manifest);

        if (response.Headers.ETag is not null)
        {
            _manifestETag = response.Headers.ETag;
        }

        if (latestVersion.CompareTo(_currentVersion) <= 0 ||
            Interlocked.CompareExchange(ref _updateStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var downloadUri = new Uri(
                $"https://github.com/iyotee/ESP32xBenchmark/releases/download/{Uri.EscapeDataString(manifest.Version)}/{AssetName}");
            var stagedExecutable = await DownloadAndVerifyAsync(
                downloadUri, manifest.Version, manifest.Sha256, cancellationToken);
            UpdateReady?.Invoke(
                this, new UpdateReadyEventArgs(manifest.Version, stagedExecutable, manifest.Sha256));
        }
        catch
        {
            Interlocked.Exchange(ref _updateStarted, 0);
            _manifestETag = null;
            throw;
        }
    }

    private static DatedReleaseVersion ValidateManifest(LatestReleaseManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Asset) ||
            string.IsNullOrWhiteSpace(manifest.Sha256) ||
            !DatedReleaseVersion.TryParseTag(manifest.Version, out var version) ||
            !string.Equals(manifest.Asset, AssetName, StringComparison.Ordinal) ||
            manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The GitHub update manifest contains invalid release metadata.");
        }

        return version;
    }

    private async Task<string> DownloadAndVerifyAsync(
        Uri downloadUri,
        string versionTag,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESP32xBenchmark", "updates", versionTag);
        Directory.CreateDirectory(updateDirectory);
        var executablePath = Path.Combine(updateDirectory, AssetName);
        if (File.Exists(executablePath) &&
            string.Equals(await GetSha256Async(executablePath, cancellationToken),
                expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return executablePath;
        }

        if (File.Exists(executablePath))
        {
            File.Delete(executablePath);
        }

        var partialPath = executablePath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await _httpClient.GetAsync(
                downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumAssetSize)
            {
                throw new InvalidDataException("The update executable exceeds the permitted download size.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                             partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaximumAssetSize)
                    {
                        throw new InvalidDataException("The update executable exceeds the permitted download size.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }

            var actualSha256 = await GetSha256Async(partialPath, cancellationToken);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update failed its SHA-256 integrity check.");
            }

            File.Move(partialPath, executablePath, overwrite: true);
            return executablePath;
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static async Task<string> GetSha256Async(
        string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed class LatestReleaseManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("asset")]
        public string Asset { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;
    }
}

public static class PortableUpdateInstaller
{
    private const string HelperPrefix = "update-helper-";

    public static void Start(string stagedExecutable, string expectedSha256)
    {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) ||
            !string.Equals(Path.GetExtension(currentExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Automatic updates require the portable Windows executable.");
        }

        EnsureInstallDirectoryIsWritable(currentExecutable);
        var helperDirectory = GetHelperDirectory();
        Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, HelperPrefix + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(currentExecutable, helperPath);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = helperDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(Path.GetFullPath(stagedExecutable));
            startInfo.ArgumentList.Add(Path.GetFullPath(currentExecutable));
            startInfo.ArgumentList.Add(expectedSha256);
            using var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The update helper process could not be started.");
        }
        catch
        {
            File.Delete(helperPath);
            throw;
        }
    }

    public static void ApplyAndRestart(string[] arguments)
    {
        if (arguments.Length != 5 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId) ||
            parentProcessId < 1 ||
            arguments[4].Length != 64 ||
            !arguments[4].All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The update helper received invalid arguments.");
        }

        var stagedExecutable = Path.GetFullPath(arguments[2]);
        var targetExecutable = Path.GetFullPath(arguments[3]);
        EnsureStagedExecutableIsInUpdateCache(stagedExecutable);
        WaitForParentProcess(parentProcessId, targetExecutable);
        if (!File.Exists(stagedExecutable) || !File.Exists(targetExecutable))
        {
            throw new FileNotFoundException("The staged update or installed application executable is missing.");
        }

        var replacementPath = targetExecutable + "." + Guid.NewGuid().ToString("N") + ".new";
        var backupPath = targetExecutable + "." + Guid.NewGuid().ToString("N") + ".bak";
        Process? updatedProcess = null;
        try
        {
            File.Copy(stagedExecutable, replacementPath);
            VerifySha256(replacementPath, arguments[4]);
            File.Replace(replacementPath, targetExecutable, backupPath, ignoreMetadataErrors: true);

            var startInfo = new ProcessStartInfo
            {
                FileName = targetExecutable,
                WorkingDirectory = Path.GetDirectoryName(targetExecutable)!,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--cleanup-update-helper");
            startInfo.ArgumentList.Add(Environment.ProcessPath
                ?? throw new InvalidOperationException("The update helper executable path is unavailable."));
            updatedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The updated application could not be restarted.");
            if (!updatedProcess.WaitForInputIdle((int)TimeSpan.FromSeconds(30).TotalMilliseconds) ||
                updatedProcess.HasExited)
            {
                throw new InvalidOperationException("The updated application did not start successfully.");
            }

            File.Delete(stagedExecutable);
            File.Delete(backupPath);
        }
        catch
        {
            if (updatedProcess is { HasExited: false })
            {
                try
                {
                    updatedProcess.Kill();
                    updatedProcess.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
                }
                catch (InvalidOperationException) when (updatedProcess.HasExited)
                {
                    Trace.TraceInformation("The updated application exited during rollback.");
                }
            }

            if (File.Exists(backupPath))
            {
                File.Replace(backupPath, targetExecutable, null, ignoreMetadataErrors: true);
            }

            if (File.Exists(targetExecutable))
            {
                var restartInfo = new ProcessStartInfo
                {
                    FileName = targetExecutable,
                    WorkingDirectory = Path.GetDirectoryName(targetExecutable)!,
                    UseShellExecute = true
                };
                using var restarted = Process.Start(restartInfo)
                    ?? throw new InvalidOperationException("The previous application could not be restarted.");
            }

            throw;
        }
        finally
        {
            updatedProcess?.Dispose();
            if (File.Exists(replacementPath))
            {
                File.Delete(replacementPath);
            }
        }
    }

    public static void ScheduleHelperCleanup(string helperPath)
    {
        var fullPath = Path.GetFullPath(helperPath);
        var helperDirectory = Path.GetFullPath(GetHelperDirectory()) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(helperDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(HelperPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update helper cleanup path is outside the update cache.");
        }

        _ = CleanupHelperAsync(fullPath);
    }

    private static async Task CleanupHelperAsync(string helperPath)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                if (File.Exists(helperPath))
                {
                    File.Delete(helperPath);
                }

                return;
            }
            catch (IOException error)
            {
                if (attempt == 29)
                {
                    Trace.TraceError($"Could not remove the completed update helper: {error.Message}");
                }
            }
            catch (UnauthorizedAccessException error)
            {
                Trace.TraceError($"Could not remove the completed update helper: {error.Message}");
                return;
            }
        }
    }

    private static void WaitForParentProcess(int processId, string targetExecutable)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The application may have exited before the helper began waiting.
            return;
        }

        using (parent)
        {
            if (parent.HasExited)
            {
                return;
            }

            var parentExecutable = parent.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(parentExecutable) ||
                !string.Equals(Path.GetFullPath(parentExecutable), targetExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update helper parent process does not match the application.");
            }

            if (!parent.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new TimeoutException("The application did not close in time to apply the update.");
            }
        }
    }

    private static void EnsureStagedExecutableIsInUpdateCache(string stagedExecutable)
    {
        var updateDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESP32xBenchmark", "updates")) + Path.DirectorySeparatorChar;
        if (!stagedExecutable.StartsWith(updateDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(stagedExecutable), "ESP32xBenchmark-win-x64.exe",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged update executable is outside the expected update cache.");
        }
    }

    private static void VerifySha256(string filePath, string expectedSha256)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staged update failed its SHA-256 integrity check.");
        }
    }

    private static void EnsureInstallDirectoryIsWritable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("The application executable has no parent directory.");
        var probePath = Path.Combine(directory, ".esp32xb-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static string GetHelperDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ESP32xBenchmark", "update-helper");
}

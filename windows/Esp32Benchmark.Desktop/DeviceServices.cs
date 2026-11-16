using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json.Nodes;
using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public sealed class SerialDeviceService
{
    public IReadOnlyList<string> GetPorts() =>
        SerialPort.GetPortNames()
            .OrderBy(PortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Task<JsonObject> IdentifyAsync(string portName, int baudRate, TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Identify(portName, baudRate, timeout, cancellationToken), cancellationToken);

    public Task<JsonObject> CaptureAsync(string portName, int baudRate, int runCount,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(portName, baudRate, runCount, timeout, cancellationToken),
            cancellationToken);

    public SerialConsoleSession OpenConsole(string portName, int baudRate) =>
        new(portName, baudRate);

    private static JsonObject Identify(string portName, int baudRate, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var serial = Open(portName, baudRate, 250);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var identity = SerialCaptureAccumulator.ReadDeviceIdentity(serial.ReadLine());
                if (identity is not null)
                {
                    return identity;
                }
            }
            catch (TimeoutException)
            {
                // Continue until the firmware has had time to boot and emit its identity record.
            }
        }

        throw new TimeoutException(
            $"No ESP32xBenchmark identity was received from {portName}. " +
            "Check the port/baud rate and reset a board running the benchmark firmware.");
    }

    private static JsonObject Capture(string portName, int baudRate, int runCount,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (runCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runCount), "Run count must be at least one.");
        }

        var accumulator = new SerialCaptureAccumulator(runCount);
        using var serial = Open(portName, baudRate, 500);
        var deadline = Stopwatch.StartNew();
        while (!accumulator.IsComplete && deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                accumulator.ConsumeLine(serial.ReadLine());
            }
            catch (TimeoutException)
            {
                // SerialPort's bounded read timeout lets us check the overall capture deadline.
            }
        }

        if (!accumulator.IsComplete)
        {
            throw new TimeoutException(
                $"Received {accumulator.CompletedRuns} of {runCount} complete benchmark runs " +
                $"from {portName} within {timeout.TotalSeconds:0} seconds. " +
                "Check the selected port and baud rate, then reset the board.");
        }

        return accumulator.BuildResult();
    }

    private static SerialPort Open(string portName, int baudRate, int readTimeout)
    {
        try
        {
            var serial = new SerialPort(portName, baudRate)
            {
                ReadTimeout = readTimeout,
                WriteTimeout = 1500,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                NewLine = "\n"
            };
            serial.Open();
            serial.DiscardInBuffer();
            return serial;
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException(
                $"{portName} is already in use. Close the serial console or other monitor and retry.",
                error);
        }
        catch (ArgumentException error)
        {
            throw new IOException($"The serial port '{portName}' is not available.", error);
        }
    }

    private static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var port) ? port : int.MaxValue;
    }
}

public sealed class SerialConsoleSession : IDisposable
{
    private readonly SerialPort _serial;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _writeLock = new();
    private readonly Task _reader;

    public SerialConsoleSession(string portName, int baudRate)
    {
        try
        {
            _serial = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 250,
                WriteTimeout = 1500,
                Encoding = new UTF8Encoding(false),
                NewLine = "\n"
            };
            _serial.Open();
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException(
                $"{portName} is already in use. Close the serial console or other monitor and retry.",
                error);
        }
        catch (ArgumentException error)
        {
            throw new IOException($"The serial port '{portName}' is not available.", error);
        }

        _reader = Task.Run(ReadLoop);
    }

    public event Action<string>? TextReceived;
    public event Action<Exception>? ReaderFailed;

    public void SendLine(string text)
    {
        lock (_writeLock)
        {
            if (!_serial.IsOpen)
            {
                throw new IOException("The serial console is disconnected.");
            }

            _serial.WriteLine(text);
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    TextReceived?.Invoke(_serial.ReadLine());
                }
                catch (TimeoutException)
                {
                    // Poll cancellation at least four times per second.
                }
            }
        }
        catch (Exception error) when (_cancellation.IsCancellationRequested ||
                                      error is IOException or InvalidOperationException)
        {
            if (!_cancellation.IsCancellationRequested)
            {
                ReaderFailed?.Invoke(error);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_serial.IsOpen)
        {
            _serial.Close();
        }

        _serial.Dispose();
        _cancellation.Dispose();
    }
}

public static class PlatformIoRunner
{
    public static async Task BuildAsync(string projectDirectory, string environment,
        string? uploadPort, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(projectDirectory, "platformio.ini");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"No platformio.ini was found in {projectDirectory}. Select the PlatformIO project folder.");
        }

        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new InvalidDataException("Select a PlatformIO firmware environment.");
        }

        var start = new ProcessStartInfo
        {
            FileName = "pio",
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(environment);
        if (!string.IsNullOrWhiteSpace(uploadPort))
        {
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("upload");
            start.ArgumentList.Add("--upload-port");
            start.ArgumentList.Add(uploadPort);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new IOException("PlatformIO did not start.");
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new IOException(
                "PlatformIO Core (`pio`) was not found on PATH. Install PlatformIO and its " +
                "platform/toolchain packages; they are not bundled in the portable app.",
                error);
        }

        var outputTask = PumpAsync(process.StandardOutput, progress, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"PlatformIO exited with code {process.ExitCode}. Review the build log.");
        }
    }

    private static async Task PumpAsync(StreamReader reader, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            progress.Report(line);
        }
    }
}

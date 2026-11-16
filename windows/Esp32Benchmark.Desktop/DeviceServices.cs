using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Esp32Benchmark.Core;

namespace Esp32Benchmark.Desktop;

public sealed record SerialPortDevice(string PortName, string? DeviceInstanceId);

public sealed class SerialDeviceService
{
    public IReadOnlyList<string> GetPorts() =>
        GetPortDevices().Select(device => device.PortName).ToArray();

    public IReadOnlyList<SerialPortDevice> GetPortDevices()
    {
        var devices = WindowsSerialPortDiscovery.GetPorts()
            .ToDictionary(device => device.PortName, StringComparer.OrdinalIgnoreCase);
        foreach (var port in SerialPort.GetPortNames())
        {
            devices.TryAdd(port, new SerialPortDevice(port, null));
        }

        return devices.Values
            .OrderBy(device => PortNumber(device.PortName))
            .ThenBy(device => device.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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

    private static class WindowsSerialPortDiscovery
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DicsFlagGlobal = 0x00000001;
        private const uint DiregDevice = 0x00000001;
        private const uint KeyRead = 0x00020019;
        private const uint ErrorNoMoreItems = 259;
        private static readonly IntPtr InvalidHandle = new(-1);

        public static IReadOnlyCollection<SerialPortDevice> GetPorts()
        {
            var devices = new Dictionary<string, SerialPortDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var className in new[] { "Ports", "Modem" })
            {
                foreach (var classGuid in GetClassGuids(className))
                {
                    EnumerateClass(classGuid, devices);
                }
            }

            return devices.Values.ToArray();
        }

        private static IEnumerable<Guid> GetClassGuids(string className)
        {
            var classGuids = new Guid[8];
            if (!SetupDiClassGuidsFromName(className, classGuids,
                    (uint)classGuids.Length, out var requiredCount))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Could not enumerate Windows device class '{className}'.");
            }

            return classGuids.Take((int)Math.Min(requiredCount, (uint)classGuids.Length)).ToArray();
        }

        private static void EnumerateClass(
            Guid classGuid, IDictionary<string, SerialPortDevice> devices)
        {
            var deviceSet = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent);
            if (deviceSet == InvalidHandle)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not enumerate connected Windows serial devices.");
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    var device = new DeviceInfoData
                    {
                        Size = (uint)Marshal.SizeOf<DeviceInfoData>()
                    };
                    if (!SetupDiEnumDeviceInfo(deviceSet, index, ref device))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreItems)
                        {
                            break;
                        }

                        throw new Win32Exception(error, "Could not read connected Windows serial devices.");
                    }

                    var deviceKey = SetupDiOpenDevRegKey(
                        deviceSet, ref device, DicsFlagGlobal, 0, DiregDevice, KeyRead);
                    if (deviceKey == IntPtr.Zero || deviceKey == InvalidHandle)
                    {
                        continue;
                    }

                    using var registryKey = RegistryKey.FromHandle(
                        new SafeRegistryHandle(deviceKey, ownsHandle: true));
                    if (registryKey.GetValue("PortName") is string portName && IsComPort(portName))
                    {
                        var instanceId = new StringBuilder(512);
                        var deviceInstanceId = SetupDiGetDeviceInstanceId(
                                deviceSet, ref device, instanceId, (uint)instanceId.Capacity, out _)
                            ? instanceId.ToString()
                            : null;
                        devices.TryAdd(portName, new SerialPortDevice(portName, deviceInstanceId));
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceSet);
            }
        }

        private static bool IsComPort(string name) =>
            name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DeviceInstance;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupDiClassGuidsFromNameW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiClassGuidsFromName(
            string className, [Out] Guid[] classGuidList, uint classGuidListSize, out uint requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr parentWindow, uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet, uint memberIndex, ref DeviceInfoData deviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet, ref DeviceInfoData deviceInfoData,
            StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDevRegKey",
            SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(
            IntPtr deviceInfoSet, ref DeviceInfoData deviceInfoData, uint scope,
            uint hardwareProfile, uint keyType, uint samDesired);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
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
    public static async Task<(string Chip, string? Environment, string Explanation)> DetectChipAsync(
        string projectDirectory, string portName, IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = "pio",
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "pkg", "exec", "-p", "platformio/tool-esptool@2.50400.0", "--",
                     "esptool", "--chip", "auto", "--port", portName,
                     "--connect-attempts", "3", "--after", "hard-reset", "chip-id"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new IOException("PlatformIO did not start the chip identification probe.");
            }
        }
        catch (Win32Exception error)
        {
            throw new IOException(
                "PlatformIO Core (`pio`) is required to identify the chip automatically.",
                error);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = (await standardOutput.ConfigureAwait(false)) +
                         Environment.NewLine +
                         (await standardError.ConfigureAwait(false));
            foreach (var line in output.Split(Environment.NewLine,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                progress.Report(line);
            }

            if (process.ExitCode != 0)
            {
                throw new IOException(
                    $"Could not identify the chip on {portName}. " +
                    "Check that the port is free and the board can enter its ROM bootloader.\n" +
                    string.Join(Environment.NewLine, output.Split(Environment.NewLine)
                        .Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(6)));
            }

            var chipLine = output.Split(Environment.NewLine)
                .Select(line => line.Trim())
                .FirstOrDefault(line =>
                    line.StartsWith("Chip type:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Chip is ", StringComparison.OrdinalIgnoreCase));
            if (chipLine is null)
            {
                throw new InvalidDataException(
                    "esptool completed without reporting the detected chip type.");
            }

            var chipName = chipLine.StartsWith("Chip type:", StringComparison.OrdinalIgnoreCase)
                ? chipLine["Chip type:".Length..].Trim()
                : chipLine["Chip is ".Length..].Trim();
            var recommendation = BoardEnvironmentRecommender.RecommendDetectedChip(chipName);
            return (chipName, recommendation.Environment, recommendation.Explanation);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

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

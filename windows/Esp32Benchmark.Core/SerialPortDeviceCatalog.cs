using System.Globalization;

namespace Esp32Benchmark.Core;

public sealed record SerialPortDevice(string PortName, string? DeviceInstanceId);

public static class SerialPortDeviceCatalog
{
    public static IReadOnlyList<SerialPortDevice> Merge(
        IEnumerable<string> systemPorts,
        IEnumerable<SerialPortDevice> windowsDevices)
    {
        ArgumentNullException.ThrowIfNull(systemPorts);
        ArgumentNullException.ThrowIfNull(windowsDevices);

        var devices = new Dictionary<string, SerialPortDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var portName in systemPorts)
        {
            devices.TryAdd(portName, new SerialPortDevice(portName, null));
        }

        foreach (var device in windowsDevices)
        {
            if (devices.TryGetValue(device.PortName, out var existing))
            {
                if (device.DeviceInstanceId is not null)
                {
                    devices[existing.PortName] = existing with
                    {
                        DeviceInstanceId = device.DeviceInstanceId
                    };
                }

                continue;
            }

            devices.Add(device.PortName, device);
        }

        return devices.Values
            .OrderBy(device => PortNumber(device.PortName))
            .ThenBy(device => device.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : int.MaxValue;
    }
}

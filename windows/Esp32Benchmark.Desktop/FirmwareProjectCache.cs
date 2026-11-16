using System.Security.Cryptography;
using System.Text;

namespace Esp32Benchmark.Desktop;

internal static class FirmwareProjectCache
{
    private const string ResourcePrefix = "Esp32Benchmark.Desktop.Firmware.";

    private static readonly (string RelativePath, string ResourceName)[] ProjectFiles =
    [
        ("platformio.ini", ResourcePrefix + "platformio.ini"),
        ("src/main.cpp", ResourcePrefix + "main.cpp"),
        ("src/main_esp8266.cpp", ResourcePrefix + "main_esp8266.cpp")
    ];

    public static string EnsureExtracted()
    {
        var files = ReadEmbeddedFiles();
        var version = GetVersion(files);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESP32xBenchmark", "firmware-projects", version);
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
        {
            var destination = Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) &&
                File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
            {
                continue;
            }

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return root;
    }

    public static void VerifyEmbeddedProject()
    {
        var root = EnsureExtracted();
        foreach (var (relativePath, content) in ReadEmbeddedFiles())
        {
            var destination = Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination) ||
                !File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
            {
                throw new InvalidDataException(
                    $"Embedded firmware bundle verification failed for {relativePath}.");
            }
        }
    }

    private static IReadOnlyList<(string RelativePath, byte[] Content)> ReadEmbeddedFiles()
    {
        var assembly = typeof(FirmwareProjectCache).Assembly;
        var files = new List<(string RelativePath, byte[] Content)>(ProjectFiles.Length);
        foreach (var (relativePath, resourceName) in ProjectFiles)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException(
                    $"The application is missing embedded firmware resource {resourceName}.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (buffer.Length == 0)
            {
                throw new InvalidDataException(
                    $"The embedded firmware resource for {relativePath} is empty.");
            }

            files.Add((relativePath, buffer.ToArray()));
        }

        return files;
    }

    private static string GetVersion(IReadOnlyList<(string RelativePath, byte[] Content)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relativePath, content) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            hash.AppendData(content);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

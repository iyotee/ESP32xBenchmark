namespace Esp32Benchmark.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Contains("--verify-firmware-bundle", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                FirmwareProjectCache.VerifyEmbeddedProject();
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }

            return;
        }

        base.OnStartup(e);
    }
}

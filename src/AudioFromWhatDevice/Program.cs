using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioFromWhatDevice;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!Environment.Is64BitProcess) return 2;
        if (args.Length == 2 && args[0] == "--probe")
        {
            using var monitor = new AudioMonitor();
            Thread.Sleep(1800);
            var snapshot = monitor.Latest;
            WriteSnapshot(args[1], snapshot);
            return snapshot.Error is null && snapshot.Devices.All(d => d.Error is null) ? 0 : 1;
        }

        var smoke = args.Length == 2 && args[0] == "--smoke";
        // Diagnostic runs must be testable while the previous release is still running.
        using var mutex = new Mutex(true, @"Local\AudioFromWhatDevice.Tray" + (smoke ? "." + Guid.NewGuid() : ""), out var first);
        if (!first) return 0;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new TrayApplicationContext();
        if (args.Length == 2 && args[0] == "--smoke") context.EnableSmokeTest(args[1]);
        Application.Run(context);
        return 0;
    }

    internal static void WriteSnapshot(string path, MonitorSnapshot snapshot)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(snapshot, options));
    }
}

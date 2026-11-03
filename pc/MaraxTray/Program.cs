using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace MaraxTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string settingsPath = args.Length == 2 && args[0] == "--settings" ? args[1] : TraySettings.DefaultPath;

        // one instance per settings file
        using var instance = new Mutex(true, $@"Local\MaraxTray-{settingsPath.Replace('\\', '_')}", out bool firstInstance);
        if (!firstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();

        TraySettings settings = TraySettings.Load(settingsPath);

        UdpClient client;
        try
        {
            client = new UdpClient(new IPEndPoint(IPAddress.Parse(settings.ListenAddress), settings.Port));
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            MessageBox.Show(
                $"UDP-Port {settings.Port} auf {settings.ListenAddress} kann nicht geöffnet werden:\n{ex.Message}",
                "MaraX Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var live = new LiveFeed();
        HistoryRecorder? recorder = null;
        WebServer? web = null;
        try
        {
            string historyPath = settings.HistoryPath.Length > 0 ? settings.HistoryPath : HistoryStore.DefaultPath;
            var store = new HistoryStore(historyPath, settings);
            recorder = new HistoryRecorder(store, settings);
            web = WebServer.Start(settings, store, live);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            // the tray icon is the main job and works without the statistics
            MessageBox.Show(
                $"Die Statistik ist nicht verfügbar, die Temperaturanzeige läuft trotzdem:\n{ex.Message}",
                "MaraX Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var autostart = new Autostart(Autostart.WindowsRunKey, Environment.ProcessPath ?? Application.ExecutablePath);
        Application.Run(new TrayContext(settings, client, autostart, live, recorder, web?.LocalUrl));
        web?.Dispose();
        return 0;
    }
}

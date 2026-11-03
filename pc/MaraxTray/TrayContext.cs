using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MaraxTray;

internal sealed class TrayContext : ApplicationContext
{
    private const int MaxTooltipLength = 127;

    private readonly UdpClient client;
    private readonly Autostart autostart;
    private readonly MachineTracker tracker;
    private readonly PacketGuard guard = new();
    private readonly LiveFeed live;
    private readonly HistoryRecorder? recorder;
    private readonly string? dashboardUrl;
    private readonly NotifyIcon notifyIcon = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem autostartItem = new("Mit Windows starten") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource stopReceiving = new();

    private Icon? currentIcon;
    private IconFace currentFace;
    private int currentIconSize;
    private bool waterWarningShown;
    private bool disposed;

    public TrayContext(
        TraySettings settings,
        UdpClient client,
        Autostart autostart,
        LiveFeed? live = null,
        HistoryRecorder? recorder = null,
        string? dashboardUrl = null)
    {
        this.client = client;
        this.autostart = autostart;
        this.live = live ?? new LiveFeed();
        this.recorder = recorder;
        this.dashboardUrl = dashboardUrl;
        tracker = new MachineTracker(settings);

        autostartItem.Click += (_, _) => autostart.Enabled = autostartItem.Checked;
        menu.Opening += (_, _) => RebuildMenu();
        RebuildMenu();

        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.DoubleClick += (_, _) => OpenDashboard();
        Refresh();
        notifyIcon.Visible = true;

        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();

        _ = ReceiveAsync();
    }

    internal string Tooltip => notifyIcon.Text;
    internal IconFace CurrentFace => currentFace;

    internal IReadOnlyList<string> MenuTexts()
    {
        RebuildMenu();
        return menu.Items.Cast<ToolStripItem>().Select(item => item.Text ?? "").ToList();
    }

    // Runs on the UI thread, the awaits return to it.
    private async Task ReceiveAsync()
    {
        while (!stopReceiving.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult received = await client.ReceiveAsync(stopReceiving.Token);
                if (MaraxPacket.TryParse(received.Buffer) is MaraxPacket parsed)
                {
                    MaraxPacket packet = guard.Check(parsed);
                    DateTime now = DateTime.UtcNow;
                    tracker.Update(packet, received.RemoteEndPoint.Address, now);
                    recorder?.Update(packet, tracker.Current(now), now);
                    Refresh();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // the listener stays alive through network changes
            }
        }
    }

    private void Refresh()
    {
        MachineView view = tracker.Current(DateTime.UtcNow);
        live.Publish(LiveState.From(view, recorder));

        string tooltip = StatusText.Tooltip(view);
        notifyIcon.Text = tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];

        IconFace face = IconFace.For(view);
        int size = SystemInformation.SmallIconSize.Width;
        if (face != currentFace || size != currentIconSize)
        {
            Icon icon = TrayIconRenderer.RenderIcon(face, size);
            notifyIcon.Icon = icon;
            currentIcon?.Dispose();
            currentIcon = icon;
            currentFace = face;
            currentIconSize = size;
        }

        if (view.WaterMissing && !waterWarningShown)
        {
            notifyIcon.ShowBalloonTip(
                10000, "MaraX: Wassertank prüfen", "Die Maschine meldet keinen Dampf-Sollwert. Das passiert, wenn sie ohne Wasser startet.", ToolTipIcon.Warning);
        }
        waterWarningShown = view.WaterMissing;
    }

    private void RebuildMenu()
    {
        menu.Items.Clear();
        foreach (string line in StatusText.Details(tracker.Current(DateTime.UtcNow)))
        {
            menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        }
        menu.Items.Add(new ToolStripSeparator());

        if (recorder?.CoffeesToday() is int coffees)
        {
            menu.Items.Add(new ToolStripMenuItem(coffees == 1 ? "Heute: 1 Kaffee" : $"Heute: {coffees} Kaffees") { Enabled = false });
        }
        if (recorder?.LastError is string error)
        {
            menu.Items.Add(new ToolStripMenuItem($"Datenerfassung gestört: {error}") { Enabled = false });
        }
        if (dashboardUrl is not null)
        {
            menu.Items.Add("Statistik öffnen", null, (_, _) => OpenDashboard());
        }

        autostartItem.Checked = autostart.Enabled;
        menu.Items.Add(autostartItem);
        menu.Items.Add("Beenden", null, (_, _) => ExitThread());
    }

    private void OpenDashboard()
    {
        if (dashboardUrl is not null)
        {
            Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            refreshTimer.Dispose();
            stopReceiving.Cancel();
            client.Dispose();
            stopReceiving.Dispose();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            menu.Dispose();
            autostartItem.Dispose();
            currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace MaraxTray.Tests;

public class IconRenderTests
{
    private static readonly MachineView Ready = new(MachinePhase.Ready, new MaraxPacket { Online = true, HxTemp = 93 }, null);
    private static readonly MachineView Heating = new(MachinePhase.Heating, new MaraxPacket { Online = true, HxTemp = 104 }, null);

    [Fact]
    public void FaceShowsTemperatureAndState()
    {
        Assert.Equal("93", IconFace.For(Ready).Text);
        Assert.Equal("104", IconFace.For(Heating).Text);
        Assert.NotEqual(IconFace.For(Ready).Background, IconFace.For(Heating).Background);
    }

    [Fact]
    public void RunningPumpHasItsOwnColor()
    {
        var pumping = Ready with { Packet = Ready.Packet! with { Pump = true } };

        Assert.Equal("93", IconFace.For(pumping).Text);
        Assert.NotEqual(IconFace.For(Ready).Background, IconFace.For(pumping).Background);
        Assert.NotEqual(IconFace.For(Heating).Background, IconFace.For(pumping).Background);
    }

    [Fact]
    public void MissingWaterHasItsOwnIcon()
    {
        var withoutWater = Heating with { WaterMissing = true };

        Assert.Equal("H2O", IconFace.For(withoutWater).Text);
        Assert.NotEqual(IconFace.For(Heating).Background, IconFace.For(withoutWater).Background);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TextStaysInsideTheIcon(int size)
    {
        foreach (string text in new[] { "9", "93", "104", "200", "AUS", "?", "--", "H2O" })
        {
            using Bitmap bitmap = TrayIconRenderer.RenderBitmap(new IconFace(text, Color.Black), size);
            int whitePixels = 0;
            for (int i = 0; i < size; i++)
            {
                foreach ((int x, int y) in new[] { (i, 0), (i, size - 1), (0, i), (size - 1, i) })
                {
                    if (bitmap.GetPixel(x, y).R > 128)
                    {
                        whitePixels++;
                    }
                }
            }
            Assert.Equal(0, whitePixels);
        }
    }

    // Writes the icons as PNG files when MARAX_ICON_DIR is set, to look at them.
    [Fact]
    public void ExportForVisualCheck()
    {
        string? folder = Environment.GetEnvironmentVariable("MARAX_ICON_DIR");
        if (folder is null)
        {
            return;
        }

        var faces = new[]
        {
            IconFace.For(new MachineView(MachinePhase.Heating, new MaraxPacket { Online = true, HxTemp = 78 }, null)),
            IconFace.For(Ready),
            IconFace.For(Ready with { Packet = Ready.Packet! with { Pump = true } }),
            IconFace.For(Heating),
            IconFace.For(new MachineView(MachinePhase.MachineOff, new MaraxPacket(), null)),
            IconFace.For(new MachineView(MachinePhase.NoData, null, null)),
            IconFace.For(new MachineView(MachinePhase.UnknownFormat, new MaraxPacket(), null)),
            IconFace.For(Heating with { WaterMissing = true }),
        };
        int[] sizes = [16, 20, 24, 32];

        const int zoom = 6;
        foreach (Color taskbar in new[] { Color.FromArgb(32, 32, 32), Color.FromArgb(243, 243, 243) })
        {
            using var sheet = new Bitmap((32 + 8) * zoom * faces.Length + 8, (32 + 8) * zoom * sizes.Length + 8);
            using Graphics graphics = Graphics.FromImage(sheet);
            graphics.Clear(taskbar);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int row = 0; row < sizes.Length; row++)
            {
                for (int column = 0; column < faces.Length; column++)
                {
                    using Bitmap icon = TrayIconRenderer.RenderBitmap(faces[column], sizes[row]);
                    graphics.DrawImage(icon, column * 40 * zoom + 8, row * 40 * zoom + 8, sizes[row] * zoom, sizes[row] * zoom);
                }
            }
            Directory.CreateDirectory(folder);
            sheet.Save(Path.Combine(folder, $"icons_{taskbar.R}.png"));
        }
    }
}

public class TrayIntegrationTests
{
    private const uint GdiObjects = 0;
    private const uint UserObjects = 1;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static string Packet(int hx, bool pump = false, int steam = 124) =>
        $$"""{"dev":"marax","v":1,"online":true,"mode":"+","fw":"1.22","hx":{{hx}},"steam":{{steam}},"target":124,"boost":0,"heat":true,"pump":{{(pump ? "true" : "false")}},"shot":{{(pump ? 7 : 0)}},"rssi":-55,"uptime":1,"bad":0,"raw":"+1.22,{{steam:000}},124,{{hx:000}},0000,1,{{(pump ? 1 : 0)}}"}""";

    // Runs the real tray context on its own UI thread with a message loop.
    private static void RunOnUiThread(
        TraySettings settings,
        Func<TrayContext, Func<string, Task>, Task> test,
        LiveFeed? live = null,
        HistoryRecorder? recorder = null,
        string? dashboardUrl = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            using var sender = new UdpClient();
            var loopback = new IPEndPoint(IPAddress.Loopback, port);

            string keyPath = $@"Software\MaraxTrayTests\{Guid.NewGuid():N}";
            var context = new TrayContext(settings, receiver, new Autostart(keyPath, @"C:\tray.exe"), live, recorder, dashboardUrl);

            async Task Send(string json)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await sender.SendAsync(bytes, bytes.Length, loopback);
            }

            async void Start()
            {
                try
                {
                    // the message loop has to run before the test can end it
                    await Task.Yield();
                    await test(context, Send);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    context.ExitThread();
                }
            }

            Start();
            Application.Run(context);
            context.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the tray test did not finish");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static async Task WaitFor(Func<bool> condition, Func<string> describe)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"timed out: {describe()}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void GoesThroughAllStatesOverRealUdp()
    {
        RunOnUiThread(new TraySettings { TimeoutSeconds = 2 }, async (tray, send) =>
        {
            Assert.Equal("MaraX: Keine Daten vom ESP", tray.Tooltip);
            Assert.Equal("--", tray.CurrentFace.Text);

            await send("""{"dev":"marax","v":1,"online":false,"pump":false,"shot":0,"rssi":-55,"uptime":1,"bad":0,"raw":""}""");
            await WaitFor(() => tray.Tooltip == "MaraX: Maschine aus", () => tray.Tooltip);
            Assert.Equal("AUS", tray.CurrentFace.Text);

            await send(Packet(hx: 70, steam: 100));
            await WaitFor(() => tray.Tooltip.StartsWith("MaraX: Heizt noch auf"), () => tray.Tooltip);
            Assert.Equal("70", tray.CurrentFace.Text);
            Color heating = tray.CurrentFace.Background;

            await send("this is not a packet");

            for (int i = 0; i < 3; i++)
            {
                await send(Packet(hx: 93));
            }
            await WaitFor(() => tray.Tooltip.StartsWith("MaraX: Aufgeheizt"), () => tray.Tooltip);
            Assert.Equal("93", tray.CurrentFace.Text);
            Color ready = tray.CurrentFace.Background;
            Assert.NotEqual(heating, ready);

            await send(Packet(hx: 93, pump: true));
            await WaitFor(() => tray.Tooltip.EndsWith("Pumpe läuft (7 s)"), () => tray.Tooltip);
            Assert.NotEqual(ready, tray.CurrentFace.Background);

            await send(Packet(hx: 93));
            await WaitFor(() => tray.Tooltip.EndsWith("Pumpe aus"), () => tray.Tooltip);
            Assert.Equal(ready, tray.CurrentFace.Background);

            await WaitFor(() => tray.Tooltip == "MaraX: Keine Daten vom ESP", () => tray.Tooltip);
            Assert.Equal("--", tray.CurrentFace.Text);
        });
    }

    [Fact]
    public void MenuShowsTheCurrentState()
    {
        RunOnUiThread(new TraySettings(), async (tray, send) =>
        {
            Assert.Equal(new[] { "Keine Daten vom ESP", "", "Mit Windows starten", "Beenden" }, tray.MenuTexts());

            for (int i = 0; i < 3; i++)
            {
                await send(Packet(hx: 93));
            }
            await WaitFor(() => tray.Tooltip.StartsWith("MaraX: Aufgeheizt"), () => tray.Tooltip);

            IReadOnlyList<string> menu = tray.MenuTexts();
            Assert.Equal("Aufgeheizt", menu[0]);
            Assert.Contains("Wärmetauscher (HX): 93 °C", menu);
            Assert.Contains("ESP: 127.0.0.1, -55 dBm", menu);
            Assert.Equal("Beenden", menu[^1]);
        });
    }

    [Fact]
    public void PacketsReachTheHistoryAndTheWebPage()
    {
        string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
        var settings = new TraySettings();
        var store = new HistoryStore(Path.Combine(folder, "history.db"), settings);
        var live = new LiveFeed();

        try
        {
            RunOnUiThread(
                settings,
                async (tray, send) =>
                {
                    await send(Packet(hx: 70, steam: 100));
                    await WaitFor(() => live.Current.Hx == 70, () => $"{live.Current.Hx}");

                    Assert.Single(store.Sessions(0, 10));
                    Assert.Equal("Heating", live.Current.Phase);
                    Assert.NotNull(live.Current.SessionId);

                    IReadOnlyList<string> menu = tray.MenuTexts();
                    Assert.Contains("Heute: 0 Kaffees", menu);
                    Assert.Contains("Statistik öffnen", menu);
                },
                live,
                new HistoryRecorder(store, settings),
                "http://localhost:1/");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WithoutWebServerTheMenuHasNoStatisticsEntry()
    {
        RunOnUiThread(new TraySettings(), (tray, _) =>
        {
            Assert.DoesNotContain("Statistik öffnen", tray.MenuTexts());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ManyUpdatesDoNotLeakHandles()
    {
        RunOnUiThread(new TraySettings(), async (tray, send) =>
        {
            async Task Burst(int count, int offset)
            {
                for (int i = 0; i < count; i++)
                {
                    int hx = 40 + (i + offset) % 2 + i % 50;
                    await send(Packet(hx));
                    await WaitFor(() => tray.CurrentFace.Text == hx.ToString(), () => tray.CurrentFace.Text);
                }
            }

            await Burst(200, 0);
            IntPtr process = Process.GetCurrentProcess().Handle;
            uint gdiBefore = GetGuiResources(process, GdiObjects);
            uint userBefore = GetGuiResources(process, UserObjects);

            await Burst(3000, 1);

            long gdiGrowth = (long)GetGuiResources(process, GdiObjects) - gdiBefore;
            long userGrowth = (long)GetGuiResources(process, UserObjects) - userBefore;
            Assert.True(gdiGrowth < 20, $"GDI objects grew by {gdiGrowth}");
            Assert.True(userGrowth < 20, $"USER objects grew by {userGrowth}");
        });
    }
}

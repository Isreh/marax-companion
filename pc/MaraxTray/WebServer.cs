using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace MaraxTray;

// Serves the statistics page and its data. Everything here only reads, the page can't change anything.
internal sealed class WebServer : IDisposable
{
    private const int MaxRows = 2000;
    private const int MaxChartPoints = 1500;

    private readonly WebApplication app;

    private WebServer(WebApplication app, int port)
    {
        this.app = app;
        Port = port;
    }

    public int Port { get; }

    public string LocalUrl => $"http://localhost:{Port}/";

    public static WebServer Start(TraySettings settings, HistoryStore store, LiveFeed live)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Parse(settings.WebListenAddress), settings.WebPort));

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self' 'unsafe-inline'; img-src 'self' data:";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        MapPage(app);
        MapData(app, store, live);
        MapExports(app, store);
        app.MapGet("/api/config", () => new
        {
            settings.MinShotSeconds,
            settings.MaxShotSeconds,
            settings.ColdStartHxTemp,
        });

        Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();
        return new WebServer(app, new Uri(app.Urls.First()).Port);
    }

    public void Dispose()
    {
        // Task.Run: the UI thread must not wait for work that wants to return to it
        Task.Run(() => app.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    private static void MapPage(IEndpointRouteBuilder routes)
    {
        string html;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MaraxTray.Web.index.html")!)
        using (var reader = new StreamReader(stream))
        {
            html = reader.ReadToEnd();
        }
        routes.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
    }

    private static void MapData(IEndpointRouteBuilder routes, HistoryStore store, LiveFeed live)
    {
        routes.MapGet("/api/live", () => live.Current);
        routes.MapGet("/api/summary", (int days = 30) => store.Summary(days));
        routes.MapGet("/api/daily", (int days = 30) => store.Daily(days));
        routes.MapGet("/api/hours", (int days = 30) => store.Hours(days));
        routes.MapGet("/api/sessions", (int days = 30, int limit = 200) => store.Sessions(days, Math.Clamp(limit, 1, MaxRows)));
        routes.MapGet("/api/shots", (int days = 30, int limit = 500) => store.Shots(days, Math.Clamp(limit, 1, MaxRows)));
        routes.MapGet("/api/events", (int days = 30, int limit = 200) => store.Events(days, Math.Clamp(limit, 1, MaxRows)));
        routes.MapGet("/api/session/latest", () => SessionResult(store.Session(null, MaxChartPoints)));
        routes.MapGet("/api/session/{id:long}", (long id) => SessionResult(store.Session(id, MaxChartPoints)));
    }

    private static IResult SessionResult(SessionDetail? detail) =>
        detail is null ? Results.NotFound() : Results.Ok(detail);

    private static void MapExports(IEndpointRouteBuilder routes, HistoryStore store)
    {
        MapExport(routes, "einschaltungen.csv",
            ["Eingeschaltet", "Startzeit genau", "HX beim Start (°C)", "Aufgeheizt", "Aufheizdauer (s)", "Ausgeschaltet", "Laufzeit (s)", "Kaffees", "Pumpenläufe", "Kaltstart"],
            () => store.ExportSessions().Select(s => new object?[]
            {
                CsvExport.Time(s.StartedAt), s.StartExact, s.StartHx, CsvExport.Time(s.ReadyAt), s.HeatupSeconds,
                CsvExport.Time(s.EndedAt), s.RuntimeSeconds, s.Coffees, s.PumpRuns, s.ColdStart,
            }));

        MapExport(routes, "pumpenlaeufe.csv",
            ["Beginn", "Art", "Dauer (s)", "HX Beginn (°C)", "HX Minimum (°C)", "Dampf Beginn (°C)", "Modus", "Einschaltung"],
            () => store.ExportPumpRuns().Select(r => new object?[]
            {
                CsvExport.Time(r.StartedAt), r.KindLabel, r.DurationSeconds, r.HxStart, r.HxMin, r.SteamStart, r.Mode, r.SessionId,
            }));

        MapExport(routes, "ereignisse.csv",
            ["Zeit", "Ereignis", "Details"],
            () => store.ExportEvents().Select(e => new object?[] { CsvExport.Time(e.At), e.Label, e.Detail }));

        MapExport(routes, "messwerte.csv",
            ["Zeit", "Einschaltung", "HX (°C)", "Dampf (°C)", "Dampf Soll (°C)", "Boost (s)", "Heizelement", "Pumpe", "Modus", "WLAN (dBm)", "Verworfene Zeilen", "Rohdaten"],
            () => store.ExportSamples().Select(row =>
            {
                row[0] = CsvExport.Time((long)row[0]!);
                return row;
            }));
    }

    private static void MapExport(IEndpointRouteBuilder routes, string name, string[] headers, Func<IEnumerable<object?[]>> rows)
    {
        routes.MapGet($"/export/{name}", () => Results.Stream(
            stream => CsvExport.WriteAsync(stream, headers, rows()),
            "text/csv; charset=utf-8",
            name));
    }
}

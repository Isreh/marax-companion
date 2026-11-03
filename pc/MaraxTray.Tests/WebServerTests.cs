using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MaraxTray.Tests;

public sealed class WebServerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
    private readonly HistoryStore store;
    private readonly LiveFeed live = new();
    private readonly WebServer server;
    private readonly HttpClient http;

    public WebServerTests()
    {
        var settings = new TraySettings { WebPort = 0, WebListenAddress = "127.0.0.1" };
        store = new HistoryStore(Path.Combine(folder, "history.db"), settings);
        server = WebServer.Start(settings, store, live);
        http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
    }

    public void Dispose()
    {
        http.Dispose();
        server.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    private long AddFinishedSession()
    {
        DateTime start = DateTime.Today.AddHours(8).ToUniversalTime();
        long id = store.AddSession(start, true, 25, 40, "1.22", start.AddHours(1));
        store.SetReady(id, start.AddMinutes(20));
        store.AddSample(id, start.AddMinutes(1), new MaraxPacket { HxTemp = 30, SteamTemp = 45, SteamTarget = 124, Mode = "+", RawLine = "+1.22,045,124,030,0900,1,0" });
        store.AddPumpRun(new PumpRunRow(id, start.AddMinutes(30), TimeSpan.FromSeconds(27.5), 93, 90, 124, "+"));
        store.AddEvent(start, "water_missing", "+1.22;E2");
        store.CloseSession(id, start.AddHours(1));
        return id;
    }

    [Fact]
    public async Task ServesThePage()
    {
        HttpResponseMessage response = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("MaraX", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LiveShowsWhatTheTrayPublished()
    {
        live.Publish(new LiveState { Phase = "Ready", Headline = "Aufgeheizt", Hx = 93, Pump = true, Revision = 7 });

        JsonElement json = await http.GetFromJsonAsync<JsonElement>("/api/live");

        Assert.Equal("Ready", json.GetProperty("phase").GetString());
        Assert.Equal(93, json.GetProperty("hx").GetInt32());
        Assert.True(json.GetProperty("pump").GetBoolean());
        Assert.Equal(7, json.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData("/api/summary")]
    [InlineData("/api/summary?days=7")]
    [InlineData("/api/daily?days=90")]
    [InlineData("/api/hours?days=0")]
    [InlineData("/api/sessions")]
    [InlineData("/api/shots?limit=999999")]
    [InlineData("/api/events?days=1")]
    public async Task EveryEndpointWorksWithoutData(string url)
    {
        HttpResponseMessage response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SummaryContainsTheCollectedData()
    {
        AddFinishedSession();

        JsonElement json = await http.GetFromJsonAsync<JsonElement>("/api/summary?days=7");

        Assert.Equal(1, json.GetProperty("coffeesToday").GetInt32());
        Assert.Equal(1200, json.GetProperty("heatupToday").GetInt32());
        Assert.Equal(1200, json.GetProperty("avgHeatupSeconds").GetInt32());
    }

    [Fact]
    public async Task SessionDetailContainsCurveAndShots()
    {
        long id = AddFinishedSession();

        JsonElement json = await http.GetFromJsonAsync<JsonElement>($"/api/session/{id}");
        JsonElement latest = await http.GetFromJsonAsync<JsonElement>("/api/session/latest");

        Assert.Equal(30, json.GetProperty("samples")[0].GetProperty("hx").GetInt32());
        Assert.Equal(27.5, json.GetProperty("shots")[0].GetProperty("durationSeconds").GetDouble());
        Assert.Equal(id, latest.GetProperty("session").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task UnknownSessionIsNotFound()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/session/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/session/latest")).StatusCode);
    }

    [Fact]
    public async Task ThePageCanOnlyRead()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("/api/live", new StringContent(""))).StatusCode);
    }

    [Theory]
    [InlineData("einschaltungen.csv", "Eingeschaltet;Startzeit genau")]
    [InlineData("pumpenlaeufe.csv", "Beginn;Art;Dauer (s)")]
    [InlineData("ereignisse.csv", "Zeit;Ereignis;Details")]
    [InlineData("messwerte.csv", "Zeit;Einschaltung;HX")]
    public async Task ExportsAreCsvForGermanExcel(string file, string header)
    {
        AddFinishedSession();

        HttpResponseMessage response = await http.GetAsync($"/export/{file}");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.StartsWith(header, Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task ExportValuesAreSafeForExcel()
    {
        AddFinishedSession();

        string runs = await http.GetStringAsync("/export/pumpenlaeufe.csv");
        string samples = await http.GetStringAsync("/export/messwerte.csv");
        string events = await http.GetStringAsync("/export/ereignisse.csv");

        Assert.Contains(";27,5;", runs);
        Assert.Contains(";'+;", runs);
        Assert.Contains(";'+1.22,045,124,030,0900,1,0", samples);
        Assert.Contains("\"'+1.22;E2\"", events);
    }
}

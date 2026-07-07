using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using MudBlazor.Services;
using Prometheus;
using System.Text.Json;
using Racearr.Core;
using Racearr.Web;
using Racearr.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Bootstrap configuration from the environment (drop-in compatible with the Python service). The
// environment seeds the DB on first run; after that the persisted connections + tunables win.
var envOptions = RacearrOptions.FromEnvironment();

// SQLite database (settings + history): a single file, mounted as a volume like the *arr apps.
var dbPath = Environment.GetEnvironmentVariable("DB_PATH");
if (string.IsNullOrWhiteSpace(dbPath)) dbPath = "/config/racearr.db";
// Require an absolute path: a relative one resolves under the (root-owned) working directory, which
// the non-root container user cannot write — fail closed with a clear message instead of crashing.
if (!Path.IsPathFullyQualified(dbPath))
{
    Console.Error.WriteLine($"racearr: DB_PATH must be an absolute path (got '{dbPath}').");
    Environment.Exit(2);
}
var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
var connectionString = $"Data Source={dbPath}";

// Migrate, then seed the tunable knobs AND the service connections from environment defaults on
// first run. After that the database is the source of truth; connection edits apply on restart
// (the effective config is resolved once, here).
IReadOnlyDictionary<string, string> settings;
Dictionary<string, string?> connValues = new(StringComparer.Ordinal);
using (var seedDb = new RacearrDbContext(new DbContextOptionsBuilder<RacearrDbContext>().UseSqlite(connectionString).Options))
{
    seedDb.Database.Migrate();

    var existing = seedDb.Settings.Select(s => s.Key).ToHashSet();
    foreach (var (key, value) in envOptions.TunableSettings())
        if (!existing.Contains(key))
            seedDb.Settings.Add(new Setting { Key = key, Value = value });

    // Seed one connection row per kind the environment configures (arr only when its key is set;
    // qBittorrent always, since it is credential-optional). Never re-seed a kind that already exists.
    var presentKinds = seedDb.Connections.Select(c => c.Kind).ToHashSet();
    void SeedConn(string kind, string? url, string? apiKey, string? user, string? pass)
    {
        if (!presentKinds.Contains(kind))
            seedDb.Connections.Add(new Connection { Kind = kind, Url = url ?? "", ApiKey = apiKey, Username = user, Password = pass });
    }
    if (!string.IsNullOrWhiteSpace(envOptions.RadarrApiKey)) SeedConn(ConnectionKinds.Radarr, envOptions.RadarrUrl, envOptions.RadarrApiKey, null, null);
    if (!string.IsNullOrWhiteSpace(envOptions.SonarrApiKey)) SeedConn(ConnectionKinds.Sonarr, envOptions.SonarrUrl, envOptions.SonarrApiKey, null, null);
    SeedConn(ConnectionKinds.Qbittorrent, envOptions.QbitUrl, null, envOptions.QbitUsername, envOptions.QbitPassword);

    seedDb.SaveChanges();
    settings = seedDb.Settings.ToDictionary(s => s.Key, s => s.Value);

    // The persisted connections are the effective service config (a disabled arr surfaces no key,
    // so it is skipped by ArrInstance.FromOptions).
    foreach (var c in seedDb.Connections.ToList())
        switch (c.Kind)
        {
            case ConnectionKinds.Radarr:
                connValues["RADARR_URL"] = c.Url;
                connValues["RADARR_API_KEY"] = c.Enabled ? c.ApiKey : "";
                break;
            case ConnectionKinds.Sonarr:
                connValues["SONARR_URL"] = c.Url;
                connValues["SONARR_API_KEY"] = c.Enabled ? c.ApiKey : "";
                break;
            case ConnectionKinds.Qbittorrent:
                connValues["QBIT_URL"] = c.Url;
                connValues["QBIT_USERNAME"] = c.Username;
                connValues["QBIT_PASSWORD"] = c.Password;
                break;
        }
}
// Effective config (resolved once at startup — connection edits apply on restart):
//   connection keys -> the DB connections (seeded from env first run),
//   tunable knobs   -> the DB setting ?? env,
//   everything else -> env (the DRY_RUN kill switch, WEBHOOK_TOKEN, HEALTH_PORT, …).
var options = RacearrOptions.FromEnvironment(key =>
    connValues.TryGetValue(key, out var cv) ? cv
    : SettingKeys.Tunable.Contains(key) ? settings.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(key)
    : Environment.GetEnvironmentVariable(key));

// Fail closed, like the Python service (SystemExit(2)): refuse to start with no *arr instance
// (from the DB connections or the environment) rather than run as a healthy-looking do-nothing pod.
if (!options.HasAnyInstance)
{
    Console.Error.WriteLine("racearr: no *arr instance configured — set RADARR_API_KEY and/or SONARR_API_KEY, or add a connection.");
    Environment.Exit(2);
}

builder.Services.AddDbContextFactory<RacearrDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddSingleton<ISettingsStore, DbSettingsStore>();
builder.Services.AddSingleton<IConnectionStore, DbConnectionStore>();
builder.Services.AddSingleton<IEventSink, DbEventSink>();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new RaceEngineState(options.DryRun));
builder.Services.AddSingleton(_ => new RacearrMetrics(Metrics.DefaultFactory));
builder.Services.AddSingleton<IEngineMetrics>(sp => sp.GetRequiredService<RacearrMetrics>());
builder.Services.AddHttpClient<IArrClient, ArrClient>();
builder.Services.AddHttpClient<IQbitClient, QbitClient>();
builder.Services.AddHttpClient<IConnectionTester, ConnectionTester>();
builder.Services.AddSingleton<RaceEngine>();
builder.Services.AddHostedService<RaceEngineHostedService>();

// Blazor Server UI (dashboard / settings / history) + its read-side history query.
builder.Services.AddSingleton<IEventHistory, DbEventHistory>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Surface the Authentik-authenticated user in the UI (informational only — the ingress enforces
// access). The scheme trusts the forward-auth headers; requests without them stay anonymous, so the
// in-cluster /metrics, /healthz, /status and webhook callers are unaffected.
builder.Services.AddAuthentication(AuthentikHeaderHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AuthentikHeaderHandler>(
        AuthentikHeaderHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Bind the health/metrics server to HEALTH_PORT (9797 by default).
builder.WebHost.UseUrls($"http://0.0.0.0:{options.HealthPort}");

var app = builder.Build();

// Publish known metric series at 0 and refresh gauges from live state on every scrape.
var metrics = app.Services.GetRequiredService<RacearrMetrics>();
var state = app.Services.GetRequiredService<RaceEngineState>();
metrics.PreInitialize(options);
Metrics.DefaultRegistry.AddBeforeCollectCallback(() => metrics.RefreshGauges(state));

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", (RaceEngineState s, RacearrOptions o) =>
{
    // Liveness: fail if the control loop has gone stale so a wedged pod is restarted
    // (matches the Python service). The first tick is exempt (loops == 0).
    var fresh = s.Loops == 0 || s.LastLoopAgeSeconds < Math.Max(60, o.PollSeconds * 4);
    return fresh ? Results.Text("ok\n") : Results.Text("stale\n", statusCode: 503);
});
app.MapGet("/status", (RaceEngineState s) => Results.Json(s.Snapshot()));
app.MapMetrics(); // Prometheus exposition at /metrics

// Seerr (Overseerr-compatible) webhook receiver: records a `request` history event. Informational
// only — it never triggers grabs or kills. Antiforgery-exempt (called by Seerr, not the Blazor app).
app.MapPost("/api/webhook/seerr", async (HttpRequest req, IEventSink events, RacearrOptions o) =>
{
    if (!SeerrWebhook.IsAuthorized(o.WebhookToken, req.Headers["X-Webhook-Token"].ToString()))
        return Results.Unauthorized();
    // A webhook payload is tiny. Cap the body at 64 KB on the actual bytes read (this covers both
    // Content-Length and chunked/unknown-length requests). Fail closed: if the cap cannot be set,
    // refuse rather than fall back to the server's much larger default.
    var bodySize = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodySize is null || bodySize.IsReadOnly)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    bodySize.MaxRequestBodySize = 64 * 1024;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var evt = SeerrWebhook.Parse(doc.RootElement);
        if (evt is not null) events.Record(evt);
    }
    catch (BadHttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); // body exceeded the cap
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }
    return Results.NoContent();
}).DisableAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

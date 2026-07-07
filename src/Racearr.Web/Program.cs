using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using MudBlazor.Services;
using Prometheus;
using System.Text.Json;
using Racearr.Core;
using Racearr.Web;
using Racearr.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Bootstrap configuration from the environment (drop-in compatible with the Python service).
var envOptions = RacearrOptions.FromEnvironment();

// Fail closed, like the Python service (SystemExit(2)): refuse to start with no *arr instance,
// so a misconfiguration is a visible crash rather than a healthy-looking do-nothing pod.
if (!envOptions.HasAnyInstance)
{
    Console.Error.WriteLine("racearr: no *arr instance configured — set RADARR_API_KEY and/or SONARR_API_KEY.");
    Environment.Exit(2);
}

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

// Migrate + seed the tunable settings from the environment defaults, then resolve the effective
// options (a persisted value wins; secrets and URLs stay environment-only).
IReadOnlyDictionary<string, string> settings;
using (var seedDb = new RacearrDbContext(new DbContextOptionsBuilder<RacearrDbContext>().UseSqlite(connectionString).Options))
{
    seedDb.Database.Migrate();
    var existing = seedDb.Settings.Select(s => s.Key).ToHashSet();
    foreach (var (key, value) in envOptions.TunableSettings())
        if (!existing.Contains(key))
            seedDb.Settings.Add(new Setting { Key = key, Value = value });
    seedDb.SaveChanges();
    settings = seedDb.Settings.ToDictionary(s => s.Key, s => s.Value);
}
// Effective config: a persisted value wins ONLY for tunable knobs; secrets and connection URLs are
// always read straight from the environment and never served from the database.
var options = RacearrOptions.FromEnvironment(key =>
    SettingKeys.Tunable.Contains(key)
        ? settings.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable(key)
        : Environment.GetEnvironmentVariable(key));

builder.Services.AddDbContextFactory<RacearrDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddSingleton<ISettingsStore, DbSettingsStore>();
builder.Services.AddSingleton<IEventSink, DbEventSink>();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new RaceEngineState(options.DryRun));
builder.Services.AddSingleton(_ => new RacearrMetrics(Metrics.DefaultFactory));
builder.Services.AddSingleton<IEngineMetrics>(sp => sp.GetRequiredService<RacearrMetrics>());
builder.Services.AddHttpClient<IArrClient, ArrClient>();
builder.Services.AddHttpClient<IQbitClient, QbitClient>();
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

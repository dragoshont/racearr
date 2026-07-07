using Prometheus;
using Racearr.Core;
using Racearr.Web;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from the environment (drop-in compatible with the Python service).
var options = RacearrOptions.FromEnvironment();

// Fail closed, like the Python service (SystemExit(2)): refuse to start with no *arr instance,
// so a misconfiguration is a visible crash rather than a healthy-looking do-nothing pod.
if (!options.HasAnyInstance)
{
    Console.Error.WriteLine("racearr: no *arr instance configured — set RADARR_API_KEY and/or SONARR_API_KEY.");
    Environment.Exit(2);
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new RaceEngineState(options.DryRun));
builder.Services.AddSingleton(_ => new RacearrMetrics(Metrics.DefaultFactory));
builder.Services.AddSingleton<IEngineMetrics>(sp => sp.GetRequiredService<RacearrMetrics>());
builder.Services.AddHttpClient<IArrClient, ArrClient>();
builder.Services.AddHttpClient<IQbitClient, QbitClient>();
builder.Services.AddSingleton<RaceEngine>();
builder.Services.AddHostedService<RaceEngineHostedService>();

// Bind the health/metrics server to HEALTH_PORT (9797 by default).
builder.WebHost.UseUrls($"http://0.0.0.0:{options.HealthPort}");

var app = builder.Build();

// Publish known metric series at 0 and refresh gauges from live state on every scrape.
var metrics = app.Services.GetRequiredService<RacearrMetrics>();
var state = app.Services.GetRequiredService<RaceEngineState>();
metrics.PreInitialize(options);
Metrics.DefaultRegistry.AddBeforeCollectCallback(() => metrics.RefreshGauges(state));

app.MapGet("/healthz", (RaceEngineState s, RacearrOptions o) =>
{
    // Liveness: fail if the control loop has gone stale so a wedged pod is restarted
    // (matches the Python service). The first tick is exempt (loops == 0).
    var fresh = s.Loops == 0 || s.LastLoopAgeSeconds < Math.Max(60, o.PollSeconds * 4);
    return fresh ? Results.Text("ok\n") : Results.Text("stale\n", statusCode: 503);
});
app.MapGet("/status", (RaceEngineState s) => Results.Json(s.Snapshot()));
app.MapMetrics(); // Prometheus exposition at /metrics

app.Run();

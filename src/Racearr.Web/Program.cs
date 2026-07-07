using Prometheus;
using Racearr.Core;
using Racearr.Web;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from the environment (drop-in compatible with the Python service).
var options = RacearrOptions.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new RaceEngineState(options.DryRun));
builder.Services.AddSingleton(_ => new RacearrMetrics(Metrics.DefaultFactory));
builder.Services.AddHostedService<RaceEngineHostedService>();

// Bind the health/metrics server to HEALTH_PORT (9797 by default).
builder.WebHost.UseUrls($"http://0.0.0.0:{options.HealthPort}");

var app = builder.Build();

// Publish known metric series at 0 and refresh gauges from live state on every scrape.
var metrics = app.Services.GetRequiredService<RacearrMetrics>();
var state = app.Services.GetRequiredService<RaceEngineState>();
metrics.PreInitialize(options);
Metrics.DefaultRegistry.AddBeforeCollectCallback(() => metrics.RefreshGauges(state));

app.MapGet("/healthz", () => Results.Text("ok\n"));
app.MapGet("/status", (RaceEngineState s) => Results.Json(s.Snapshot()));
app.MapMetrics(); // Prometheus exposition at /metrics

app.Run();

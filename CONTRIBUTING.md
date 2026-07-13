# Contributing to racearr

Thanks for helping make racearr better. Issues and PRs are welcome.

## Project layout

racearr is a small .NET 10 solution ([`Racearr.slnx`](Racearr.slnx)):

- **`src/Racearr.Core`** — pure domain + decision logic (no web/IO dependencies). This
  is where the racing/SLA rules live and where most unit tests point.
- **`src/Racearr.Web`** — the ASP.NET Core host: `HttpClient` integrations
  (Radarr/Sonarr, qBittorrent), the Blazor UI, metrics, persistence, and DI wiring.
- **`tests/Racearr.Core.Tests`** — xUnit tests for both projects.

## Build and test

```bash
dotnet build          # or: dotnet build -c Release
dotnet test           # the smoke test — keep it green
```

CI runs the same build + tests, boots the container image against `/healthz`, and
publishes multi-arch images. A PR must be green before merge.

## Conventions

- **Keep `Racearr.Core` pure.** Decision logic goes in `RaceDecisions` / the engine;
  IO stays in `Racearr.Web`. This keeps the rules unit-testable without HTTP.
- **Cover new logic with a test plus at least one adversarial/edge case.** Tests should
  not need network access — build inputs directly (see the existing tests).
- **Never log or persist secrets.** API keys and the qBittorrent password stay in the
  environment; they are never written to the database, logs, or race history.
- **Respect `DRY_RUN`.** Any new mutation (grab, delete, search, notification side
  effect) must be gated so an observe-only run performs no real action.
- **Keep metric labels stable.** Series are named `racearr_*`; changing label sets
  breaks existing dashboards. The primary instances stay `radarr`/`sonarr`.
- **Integrations are configurable and additive.** New download clients, notification
  channels, or auth-proxy schemes should be opt-in via config and off by default. A
  new notification channel is a single guarded block in `IncidentNotifications.Build`
  plus its options — please add a unit test alongside it.

## Pull requests

1. Open an issue first for anything non-trivial so the approach can be agreed.
2. Keep the change focused; update the README/`.env.example` when you add config.
3. Make sure `dotnet test` is green and describe how you verified the change.

## Security

Please do not open a public issue for a vulnerability — see [`SECURITY.md`](SECURITY.md)
for private reporting.

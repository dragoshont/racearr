# ADR-0001 — Rewrite racearr as a .NET 10 *arr-native service with a web UI

- **Status:** Accepted (2026-07-07)
- **Supersedes:** the Python stdlib single-file service (`racearr.py`, released `v0.2.0`)
- **Deciders:** repo owner + engineering partner

## Context

racearr began as a ~640-line, dependency-free Python daemon that enforces two SLAs on the
Radarr/Sonarr download pipeline (pickup-within-3-min, speed-≥1 MB/s-or-race) by polling the
*arr APIs and racing alternate releases. `v0.2.0` is containerised, Flux-deployed, and verified
working in production (100 % pickup-SLA on a live 3-movie test).

The owner elected to grow racearr into a **first-class member of the *arr ecosystem** — a real
product with a web UI, not a headless script. Three paths were weighed:

- **A. Keep the lean Python engine** and add a seerr webhook, client-agnostic speed, docs.
- **B. A + a light web UI** that reads the existing `/status` + `/metrics` APIs.
- **C. A full .NET 10 rewrite with an integrated UI**, native to the *arr stack.

The partner recommended **A** on YAGNI grounds (the Python engine is done and excellent, and
"it's just one file" is a strength, not a defect). The owner chose **C** with full knowledge of
the trade-offs: the goal is an *arr-native app with a UI and long-term community fit, which
justifies the larger surface. This ADR records that decision and the guardrails around it.

## Decision

Rewrite racearr on **.NET 10 / ASP.NET Core**, mirroring the Servarr conventions:

| Concern | Decision |
| --- | --- |
| Runtime | .NET 10 (LTS), ASP.NET Core minimal host |
| Persistence | **EF Core + SQLite**, a single file mounted as a volume (like the *arr apps) — settings + race history |
| Metrics | **prometheus-net**, exposing metric names/labels/buckets **byte-for-byte identical** to the Python service |
| Engine | Racing/SLA logic lives in **`Racearr.Core`** (no web deps, unit-tested) and runs as a `BackgroundService` in `Racearr.Web` |
| Config | Drop-in compatible with the Python **environment contract** (`RADARR_*`, `SONARR_*`, `POLL_SECONDS`, `PICKUP_SLA_SECONDS`, …); config file/UI added later |
| Layout | `src/Racearr.Core`, `src/Racearr.Web`, `tests/Racearr.Core.Tests` |

### API contract targets (verified against the live homelab, 2026-07-07)

| Service | Version | API | Client |
| --- | --- | --- | --- |
| Radarr | `6.2.1.10461` | **v3** (`/api/v3`) | one generic `ArrClient` — Radarr & Sonarr share the v3 surface |
| Sonarr | `4.0.17.2952` | **v3** (`/api/v3`) | same `ArrClient`, per-kind search param (`movieId`/`episodeId`) |
| Seerr  | `3.3.0` (`seerr-team/seerr`, **not** Overseerr) | **v1** (`/api/v1`) | webhook receiver + optional `/api/v1/request` client |

Clients target the **API contract (v3 / v1), not the exact versions** — upgrade-safe across the
whole *arr range. Seerr integration is a `/seerr-webhook` receiver (validated by a shared secret)
that starts the pickup-SLA clock from the request instant; polling `/api/v1/request` is an
optional fallback. **No Plex / Plex Pass dependency** — racearr never talks to Plex.

### Metric compatibility (non-negotiable)

The existing Grafana dashboard (uid `racearr`) and the three alert rules
(`racearr_down`, `racearr_loop_stalled`, `racearr_pickup_sla_breach`) MUST keep working after
cutover. Therefore every metric name, label set, and histogram bucket is reproduced exactly, and
the known label sets are pre-initialised to `0`. Verified at Phase 0: `/metrics` emits
`racearr_up`, `racearr_pickups_total{instance,result}`, `racearr_pickup_latency_seconds_bucket{le=…}`
etc. identically to Python.

## Cutover & reversibility

- The Python `ghcr.io/dragoshont/racearr:0.2.0` **stays live and untouched** in the cluster
  until the .NET build is proven.
- Cutover runs in `DRY_RUN=true` (observe-only) first for a soak, then arms.
- Because the image tag is pinned in Flux, rollback is a one-line revert to `:0.2.0`.

## Phased plan

0. ADR + solution scaffold + host serving `/healthz`+`/status`+`/metrics` — **done** (build+tests+runtime green).
1. Faithful engine port (pickup/speed SLA, force-grab past cutoff, cull, cooldown, baseline, protect-private) + parity tests.
2. EF Core/SQLite (settings + history) + multi-arch (`linux/amd64,linux/arm64`) image.
3. Web UI (Blazor + MudBlazor vs React SPA — decided before build) with design sign-off.
4. Seerr webhook + optional client-agnostic speed (compute from *arr queue `sizeleft` deltas).
5. Homelab cutover (image swap, DRY_RUN soak), retire Python.

## Consequences

- **Cost:** ~5–10× the code, a larger image, slower iteration, real maintenance load.
- **Benefit:** *arr-native, UI-capable, type-safe, SQLite-persistent history, community-familiar.
- **Risk:** behavioural drift from the proven Python engine — mitigated by Core parity tests and the
  DRY_RUN soak before arming.

## Rejected alternatives

- **A (keep Python):** cheapest, lowest risk, but no UI and not *arr-native. Rejected by the owner in
  favour of a real product.
- **B (Python + light UI):** avoids the rewrite but splits the stack (Python engine + separate UI) and
  never feels *arr-native. Rejected for the same reason.

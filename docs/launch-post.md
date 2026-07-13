# Launch post drafts

Two ready-to-paste drafts (r/radarr + r/selfhosted) plus a short r/unRAID variant.
Keep it honest, lead with the one thing nothing else does (parallel racing), and be
up front that it starts in observe-only mode. Swap the screenshot links for the real
imgur/GitHub URLs before posting.

---

## r/radarr (and r/sonarr)

**Title:** racearr — races several alternates for a slow download in parallel and keeps the fastest (Radarr/Sonarr + qBittorrent)

**Body:**

We've all had the grab that sits at 40 KB/s behind a dead torrent while three
perfectly good alternates sit unused in the indexer results. Existing tools mostly
*remove* the stall and re-search once. racearr does something different: it **races**.

When a download is slow to start or crawling below your speed SLA, racearr:

- forces extra interactive searches on that exact movie/episode,
- grabs several well-seeded alternates **at the same time**,
- watches live per-torrent speed, and
- keeps the fastest one, then cancels + blocklists the losers.

So the item finishes at the speed of your *best available* source, not your
unluckiest one. It's not a strike-and-remove cleaner — it adds speed by racing.

Other things it does:
- **Pickup + speed SLAs** — raises an incident if a wanted item isn't grabbed in N
  minutes, or isn't moving fast enough.
- **Season-pack aware** — a dead Sonarr season pack is remediated as a pack, never
  raced episode-by-episode.
- **Private-tracker safe** — configurable protection so it never races/kills on your
  private indexers.
- **Dead-torrent / runt / fake guards** so a 2 MB "sample" never wins a race.
- Live dashboard, Prometheus metrics, and optional **Discord / ntfy** notifications.

**It ships in `DRY_RUN=true` (observe-only).** You run it, watch the logs/dashboard
decide for a day, and only then flip `DRY_RUN=false` to arm it. Nothing gets grabbed
or removed until you do.

Stack: .NET 10, SQLite, single container. `docker-compose` + an Unraid Community
Apps template are in the repo.

Repo: https://github.com/dragoshont/racearr
Would love feedback on the racing heuristics — especially from folks on a mix of
public + private trackers.

---

## r/selfhosted

**Title:** I built racearr: instead of just removing stalled *arr downloads, it races alternates in parallel and keeps the fastest

**Body:**

Most *arr "cleanup" helpers (great tools — Cleanuparr, decluttarr, swaparr) follow a
strike-and-remove model: detect a stalled/slow download, remove + blocklist it,
trigger a re-search, repeat. That fixes *stuck*, but you still wait out a fresh
single download each time.

racearr attacks the *speed* problem instead. On a slow grab it forces extra searches,
grabs several well-seeded alternates for the same item **concurrently**, measures live
speed, keeps the fastest, and cancels the rest. You get the throughput of your best
source without babysitting the queue.

Feature summary:
- Parallel racing + keep-fastest (the core idea)
- Pickup-time and download-speed SLAs with incidents
- Season-pack-correct remediation for Sonarr
- Private-tracker protection, dead-torrent/runt/fake guards
- Blazor dashboard, Prometheus `/metrics`, Discord/ntfy notifications
- Multiple Radarr/Sonarr instances (e.g. race a 1080p and a 4K library at once)

Safety: **starts observe-only (`DRY_RUN=true`)** — it logs every decision it *would*
make until you explicitly arm it.

Runs as one container (.NET 10 + SQLite). `docker-compose.yml` and an Unraid CA
template are included; MIT licensed.

How it compares:

| Tool        | Model                         | Clients                               | Unique bit                      |
|-------------|-------------------------------|---------------------------------------|---------------------------------|
| **racearr** | **race alternates, keep fastest** | qBittorrent (Deluge/Transmission beta) | parallel racing + speed SLAs    |
| Cleanuparr  | strike + remove + re-search   | qBit/Deluge/Transmission/µTorrent/rTorrent | broad client support, mature |
| decluttarr  | strike + remove               | qBittorrent + SABnzbd                 | simple, YAML config             |
| swaparr     | strike + remove               | per-*arr container                    | tiny, Rust, env-only            |

Repo + screenshots: https://github.com/dragoshont/racearr

---

## r/unRAID (short)

**Title:** racearr — race slow *arr downloads against alternates and keep the fastest (Unraid template included)

**Body:**

racearr forces extra searches on a slow Radarr/Sonarr grab, races several well-seeded
alternates in parallel via qBittorrent, and keeps the fastest (cancelling the losers).
Not a strike-and-remove cleaner — it adds speed by racing.

- One container, `/config` appdata, WebUI on port 9797
- Starts in `DRY_RUN` observe-only; flip to arm
- Discord/ntfy notifications, Prometheus metrics, live dashboard

Add the template manually for now:
`https://raw.githubusercontent.com/dragoshont/racearr/main/unraid/racearr.xml`
(a Community Applications store submission is planned).
Repo: https://github.com/dragoshont/racearr

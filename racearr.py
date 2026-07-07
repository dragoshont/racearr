#!/usr/bin/env python3
"""racearr — aggressive download racer + SLA incident monitor for the *arr stack.

Problem it solves (see the README):
  Radarr/Sonarr grab exactly ONE release and rank by custom-format score, NOT by
  seeders, so a low-seed "high quality" release can win and crawl for hours. There
  is no minute-scale fast-fail. This service enforces two hard SLAs and races
  candidates to keep the fastest.

Contract:
  * PICKUP SLA  — an item that becomes wanted (added via Plex watchlist) must be
    grabbed within PICKUP_SLA_SECONDS, else raise an incident and force a search.
  * SPEED  SLA  — an active download must reach >= SPEED_SLA_MBPS within
    SPEED_SLA_SECONDS, else raise an incident, interactive-search for the
    highest-seeded allowed alternates, grab up to MAX_CONCURRENT_PER_ITEM of them,
    race, then keep the fastest and kill the losers.

Design rules:
  * All grabs and removals go THROUGH the *arr API (grab release, delete queue item
    with skipRedownload) so *arr always tracks the winner and imports it natively —
    no manual-import reconciliation, no fighting *arr's auto-search.
  * qBittorrent is used READ-ONLY, purely to read live per-torrent speed by hash.
  * Speed > quality: candidates are whatever the item's quality profile already
    allows (1080p-first here), sorted by seeders descending.
  * Private trackers are protected: never grab private alternates and never
    remove-from-client a private torrent (hit-and-run safety).
  * Only downloads/items first observed AFTER startup are managed, so the existing
    backlog is never touched.

Stdlib only. No third-party deps.
"""
import json
import os
import threading
import time
import logging
import urllib.request
import urllib.parse
import urllib.error
import http.cookiejar
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


# ----------------------------------------------------------------------------- config
def _s(k, d=None):
    v = os.environ.get(k)
    return v if v not in (None, "") else d


def _i(k, d):
    try:
        return int(os.environ.get(k, d))
    except (TypeError, ValueError):
        return int(d)


def _f(k, d):
    try:
        return float(os.environ.get(k, d))
    except (TypeError, ValueError):
        return float(d)


def _b(k, d):
    v = os.environ.get(k)
    return d if v is None else v.strip().lower() in ("1", "true", "yes", "on")


def _list(k, d):
    raw = _s(k, d) or ""
    return [x.strip().lower() for x in raw.split(",") if x.strip()]


POLL_SECONDS = _i("POLL_SECONDS", 12)
PICKUP_SLA_SECONDS = _i("PICKUP_SLA_SECONDS", 180)
SPEED_SLA_SECONDS = _i("SPEED_SLA_SECONDS", 120)
SPEED_SLA_MBPS = _f("SPEED_SLA_MBPS", 1.0)
RACE_TARGET_MBPS = _f("RACE_TARGET_MBPS", 2.0)
RACE_MONITOR_SECONDS = _i("RACE_MONITOR_SECONDS", 180)
RACE_CULL_AFTER_SECONDS = _i("RACE_CULL_AFTER_SECONDS", 60)
RACE_COOLDOWN_SECONDS = _i("RACE_COOLDOWN_SECONDS", 600)  # back off re-racing the same item
MAX_PER_ITEM = _i("MAX_CONCURRENT_PER_ITEM", 4)
RACE_MIN_SEEDERS = _i("RACE_MIN_SEEDERS", 3)
RACE_MAX_RES = _i("RACE_MAX_RESOLUTION", 1080)  # speed over quality: never race UHD alternates
MAX_ACTIVE_RACES = _i("MAX_ACTIVE_RACES", 6)
PROTECT_PRIVATE = _b("PROTECT_PRIVATE", True)
# Comma-separated indexer names / tracker domains to treat as private and never race
# or remove-from-client (hit-and-run safety). Empty by default — set to your private
# trackers, e.g. PRIVATE_INDEXERS="passthepopcorn,broadcasthenet".
PRIVATE_INDEXERS = _list("PRIVATE_INDEXERS", "")
PRIVATE_TRACKER_DOMAINS = _list("PRIVATE_TRACKER_DOMAINS", "")
DRY_RUN = _b("DRY_RUN", True)
INCIDENT_WEBHOOK = _s("INCIDENT_WEBHOOK_URL")
HEALTH_PORT = _i("HEALTH_PORT", 9797)
QBIT_URL = (_s("QBIT_URL", "http://localhost:8080")).rstrip("/")
QBIT_USER = _s("QBIT_USERNAME")   # optional — omit if qBittorrent bypasses auth for this client
QBIT_PASS = _s("QBIT_PASSWORD")

MB = 1024 * 1024

logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO"),
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("racearr")

INSTANCES = []
if _s("RADARR_API_KEY"):
    INSTANCES.append(dict(kind="radarr", url=_s("RADARR_URL", "http://localhost:7878").rstrip("/"),
                          key=_s("RADARR_API_KEY"), item_field="movieId", search_param="movieId",
                          search_cmd="MoviesSearch", search_ids="movieIds"))
if _s("SONARR_API_KEY"):
    INSTANCES.append(dict(kind="sonarr", url=_s("SONARR_URL", "http://localhost:8989").rstrip("/"),
                          key=_s("SONARR_API_KEY"), item_field="episodeId", search_param="episodeId",
                          search_cmd="EpisodeSearch", search_ids="episodeIds"))


# ----------------------------------------------------------------------------- http helpers
def _http(method, url, headers=None, body=None, timeout=60):
    headers = dict(headers or {})
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
        return r.status, raw


def arr_get(inst, path, params=None):
    q = ("?" + urllib.parse.urlencode(params)) if params else ""
    _, raw = _http("GET", f"{inst['url']}/api/v3/{path}{q}", {"X-Api-Key": inst["key"]})
    return json.loads(raw) if raw else None


def arr_post(inst, path, body):
    return _http("POST", f"{inst['url']}/api/v3/{path}", {"X-Api-Key": inst["key"]}, body)


def arr_delete(inst, path, params):
    q = "?" + urllib.parse.urlencode(params)
    return _http("DELETE", f"{inst['url']}/api/v3/{path}{q}", {"X-Api-Key": inst["key"]})


# qBittorrent WebUI client. Works with no credentials when qBit bypasses auth for
# this client (localhost bypass / whitelisted subnet); otherwise logs in with
# QBIT_USERNAME/QBIT_PASSWORD and keeps the session cookie, re-authenticating on 401/403.
_qbit_opener = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def qbit_login():
    if not (QBIT_USER and QBIT_PASS):
        return False
    data = urllib.parse.urlencode({"username": QBIT_USER, "password": QBIT_PASS}).encode()
    req = urllib.request.Request(f"{QBIT_URL}/api/v2/auth/login", data=data,
                                 headers={"Referer": QBIT_URL})
    try:
        ok = _qbit_opener.open(req, timeout=15).read().strip() == b"Ok."
        if not ok:
            log.warning("qbit login rejected — check QBIT_USERNAME/QBIT_PASSWORD")
        return ok
    except Exception as e:  # noqa: BLE001
        log.warning("qbit login failed: %s", e)
        return False


def qbit_get(path, timeout=30):
    url = f"{QBIT_URL}{path}"
    try:
        return _qbit_opener.open(url, timeout=timeout).read()
    except urllib.error.HTTPError as e:
        if e.code in (401, 403) and qbit_login():
            return _qbit_opener.open(url, timeout=timeout).read()
        raise


def qbit_by_hash():
    """Return {infohash_lower: torrent_dict} for live speed lookups."""
    try:
        return {t["hash"].lower(): t for t in json.loads(qbit_get("/api/v2/torrents/info"))}
    except Exception as e:  # noqa: BLE001 - never let qbit failure crash the loop
        log.warning("qbit fetch failed: %s", e)
        return {}


# ----------------------------------------------------------------------------- state
_lock = threading.Lock()
STATS = {"started": time.time(), "last_loop": 0, "loops": 0, "incidents": 0,
         "races_started": 0, "candidates_grabbed": 0, "losers_killed": 0}

DL = {}            # infohash -> {first_seen, max_speed, kind}
RACE = {}          # "kind:itemId" -> {start}
COOLDOWN = {}      # "kind:itemId" -> epoch until which re-racing is suppressed
PICKUP = {}        # "kind:itemId" -> {first_seen, alerted}
BASELINE_DL = set()      # download hashes present at startup (never managed)
BASELINE_WANTED = set()  # wanted item keys present at startup (never pickup-alerted)
_primed = False


# ----------------------------------------------------------------------------- metrics
# Minimal Prometheus text-format registry (stdlib only). Counters carry labels; a few
# histograms capture pickup latency, time-to-target-speed and race-winner speed.
_MLOCK = threading.Lock()
_COUNTERS = {}   # (name, ((k, v), ...)) -> float
_HISTS = {}      # name -> {"le": [...], "counts": [...], "sum": float, "count": int}
PICKUP_BUCKETS = [15, 30, 60, 90, 120, 180, 300, 600]
TTT_BUCKETS = [30, 60, 90, 120, 180, 300, 600]
MBPS_BUCKETS = [0.5, 1, 2, 3, 5, 8, 16, 32]


def _lk(labels):
    return tuple(sorted((labels or {}).items()))


def m_inc(name, labels=None, value=1.0):
    with _MLOCK:
        k = (name, _lk(labels))
        _COUNTERS[k] = _COUNTERS.get(k, 0.0) + value


def m_observe(name, value, buckets):
    with _MLOCK:
        h = _HISTS.get(name)
        if h is None:
            h = _HISTS[name] = {"le": list(buckets), "counts": [0] * len(buckets),
                                "sum": 0.0, "count": 0}
        h["sum"] += value
        h["count"] += 1
        for i, b in enumerate(h["le"]):
            if value <= b:
                h["counts"][i] += 1


def render_metrics():
    """Render the current metrics in Prometheus text exposition format."""
    now = time.time()
    with _lock:
        loops, last = STATS["loops"], STATS["last_loop"]
    out = []
    gauges = {
        "racearr_up": 1,
        "racearr_dry_run": 1 if DRY_RUN else 0,
        "racearr_active_races": len(RACE),
        "racearr_managed_downloads": sum(1 for h in DL if h not in BASELINE_DL),
        "racearr_loops_total": loops,
        "racearr_last_loop_age_seconds": round(now - last, 1) if last else 0,
    }
    for n, v in gauges.items():
        out.append(f"# TYPE {n} gauge")
        out.append(f"{n} {v}")
    with _MLOCK:
        counters = sorted(_COUNTERS.items())
        hists = [(n, dict(le=list(h["le"]), counts=list(h["counts"]),
                          sum=h["sum"], count=h["count"])) for n, h in _HISTS.items()]
    typed = set()
    for (name, lk), val in counters:
        if name not in typed:
            out.append(f"# TYPE {name} counter")
            typed.add(name)
        lbl = ("{" + ",".join(f'{k}="{v}"' for k, v in lk) + "}") if lk else ""
        out.append(f"{name}{lbl} {val}")
    for name, h in sorted(hists):
        out.append(f"# TYPE {name} histogram")
        for i, le in enumerate(h["le"]):
            out.append(f'{name}_bucket{{le="{le}"}} {h["counts"][i]}')
        out.append(f'{name}_bucket{{le="+Inf"}} {h["count"]}')
        out.append(f"{name}_sum {h['sum']}")
        out.append(f"{name}_count {h['count']}")
    return ("\n".join(out) + "\n").encode()


def metrics_init():
    """Pre-register the known metric series at 0 so dashboards render clean zeros
    instead of 'No data' before the first event fires. Prometheus best practice is
    to expose every known label set from process start; the label spaces here are
    small and closed (instances, pickup results, race outcomes, incident types)."""
    with _MLOCK:
        for inst in INSTANCES:
            k = inst["kind"]
            for name in ("racearr_races_started_total",
                         "racearr_candidates_grabbed_total",
                         "racearr_losers_killed_total",
                         "racearr_downloads_reached_target_total"):
                _COUNTERS.setdefault((name, _lk({"instance": k})), 0.0)
            for result in ("in_sla", "breached"):
                _COUNTERS.setdefault(
                    ("racearr_pickups_total", _lk({"instance": k, "result": result})), 0.0)
            for outcome in ("won_target", "kept_below_target"):
                _COUNTERS.setdefault(
                    ("racearr_race_outcomes_total", _lk({"instance": k, "outcome": outcome})), 0.0)
        for itype in ("pickup_sla", "speed_sla", "race_no_target"):
            _COUNTERS.setdefault(("racearr_incidents_total", _lk({"type": itype})), 0.0)
        for name, buckets in (("racearr_pickup_latency_seconds", PICKUP_BUCKETS),
                              ("racearr_time_to_target_seconds", TTT_BUCKETS),
                              ("racearr_race_winner_mbps", MBPS_BUCKETS)):
            _HISTS.setdefault(name, {"le": list(buckets), "counts": [0] * len(buckets),
                                     "sum": 0.0, "count": 0})


def incident(itype, msg, **fields):
    with _lock:
        STATS["incidents"] += 1
    m_inc("racearr_incidents_total", {"type": itype})
    rec = {"level": "INCIDENT", "type": itype, "msg": msg,
           "ts": datetime.now(timezone.utc).isoformat(), **fields}
    log.warning("INCIDENT %s", json.dumps(rec))
    if INCIDENT_WEBHOOK:
        try:
            _http("POST", INCIDENT_WEBHOOK, body={"text": f"[racearr] {itype}: {msg}"}, timeout=15)
        except Exception as e:  # noqa: BLE001
            log.warning("incident webhook failed: %s", e)


# ----------------------------------------------------------------------------- release selection
def _quality_name(r):
    return (((r.get("quality") or {}).get("quality") or {}).get("name")) or "?"


def _is_private_release(r):
    name = str(r.get("indexer") or "").lower()
    return any(p in name for p in PRIVATE_INDEXERS)


def _is_private_torrent(t):
    if not t:
        return False
    blob = (str(t.get("tracker") or "") + " " + str(t.get("magnet_uri") or "")).lower()
    return any(d in blob for d in PRIVATE_TRACKER_DOMAINS)


def _resolution(r):
    return (((r.get("quality") or {}).get("quality") or {}).get("resolution")) or 0


def _raceable_rejection(r):
    """A release is raceable if it is not rejected, or rejected ONLY because an
    equal-quality release is already in the queue ("already meets cutoff" / "not an
    upgrade"). That auto-rejection is exactly the race case — a manual POST /release
    grabs it anyway. Releases rejected for quality-not-wanted, size, or any other hard
    reason are NOT raceable and are excluded."""
    if not r.get("rejected"):
        return True
    rej = r.get("rejections") or []
    return bool(rej) and all(
        any(h in str(x).lower() for h in ("cutoff", "upgrade")) for x in rej)


def candidate_releases(inst, item_id, exclude_hashes):
    """Highest-seeded 1080p-first torrent releases suitable to race — including the
    same-quality alternates the arr would auto-reject as 'already meets cutoff'."""
    try:
        rel = arr_get(inst, "release", {inst["search_param"]: item_id})
    except Exception as e:  # noqa: BLE001
        log.warning("release search failed (%s %s): %s", inst["kind"], item_id, e)
        return []
    out = []
    for r in rel or []:
        if r.get("protocol") != "torrent":
            continue
        if (r.get("seeders") or 0) < RACE_MIN_SEEDERS:
            continue
        res = _resolution(r)
        if RACE_MAX_RES and res and res > RACE_MAX_RES:
            continue  # never race UHD alternates (speed over quality)
        if not _raceable_rejection(r):
            continue  # rejected for quality/size/other — not a valid race candidate
        if PROTECT_PRIVATE and _is_private_release(r):
            continue
        ih = str(r.get("infoHash") or "").lower()
        if ih and ih in exclude_hashes:
            continue
        out.append(r)
    out.sort(key=lambda r: -(r.get("seeders") or 0))
    return out


# ----------------------------------------------------------------------------- queue helpers
def get_queue(inst):
    params = {"page": 1, "pageSize": 400, "includeUnknownMovieItems": "false"}
    if inst["kind"] == "sonarr":
        params = {"page": 1, "pageSize": 400}
    d = arr_get(inst, "queue", params)
    return (d or {}).get("records", []) or []


def get_wanted_keys(inst):
    """Set of 'kind:itemId' for monitored-missing items (fresh wanted detection)."""
    keys = {}
    try:
        d = arr_get(inst, "wanted/missing",
                    {"page": 1, "pageSize": 200, "sortDirection": "descending",
                     "sortKey": "id", "monitored": "true"})
    except Exception as e:  # noqa: BLE001
        log.warning("wanted fetch failed (%s): %s", inst["kind"], e)
        return keys
    for rec in (d or {}).get("records", []) or []:
        iid = rec.get("id")
        title = rec.get("title") or (rec.get("movie") or {}).get("title") or "?"
        if iid is not None:
            keys[f"{inst['kind']}:{iid}"] = title
    return keys


def force_search(inst, item_id):
    if DRY_RUN:
        log.info("[dry-run] would force %s for %s %s", inst["search_cmd"], inst["kind"], item_id)
        return
    try:
        arr_post(inst, "command", {"name": inst["search_cmd"], inst["search_ids"]: [item_id]})
    except Exception as e:  # noqa: BLE001
        log.warning("force search failed (%s %s): %s", inst["kind"], item_id, e)


def grab_release(inst, r):
    title = str(r.get("title"))[:70]
    if DRY_RUN:
        log.info("[dry-run] would GRAB S=%s %s %s", r.get("seeders"), _quality_name(r), title)
        return True
    try:
        arr_post(inst, "release", {"guid": r.get("guid"), "indexerId": r.get("indexerId")})
        log.info("GRAB S=%s %s %s", r.get("seeders"), _quality_name(r), title)
        return True
    except Exception as e:  # noqa: BLE001
        log.warning("grab failed (%s): %s", title, e)
        return False


def kill_queue_record(inst, rec, qbt):
    """Remove a losing queue record + its torrent, without triggering *arr re-search.

    Private torrents are never removed from the client (hit-and-run safety); they are
    only detached from the *arr queue so they keep seeding to satisfy the tracker.
    """
    title = str(rec.get("title"))[:60]
    private = PROTECT_PRIVATE and _is_private_torrent(qbt)
    remove_client = not private
    if DRY_RUN:
        log.info("[dry-run] would KILL%s %s", " (detach-only, private)" if private else "", title)
        return
    try:
        arr_delete(inst, f"queue/{rec['id']}", {
            "removeFromClient": "true" if remove_client else "false",
            "blocklist": "true" if remove_client else "false",
            "skipRedownload": "true",
        })
        with _lock:
            if remove_client:
                STATS["losers_killed"] += 1
        if remove_client:
            m_inc("racearr_losers_killed_total", {"instance": inst["kind"]})
        log.info("KILL%s %s", " (detach-only, private)" if private else "", title)
    except Exception as e:  # noqa: BLE001
        log.warning("kill failed (%s): %s", title, e)


# ----------------------------------------------------------------------------- core per-instance pass
def process_instance(inst, qbt):
    now = time.time()
    records = get_queue(inst)

    # group active download records by *arr item id, tracking pack downloads
    groups = {}          # item_id -> list[rec]
    hash_to_items = {}   # infohash -> set(item_id)  (to detect multi-episode packs)
    for rec in records:
        iid = rec.get(inst["item_field"])
        dlid = str(rec.get("downloadId") or "").lower()
        if iid is None or not dlid:
            continue
        groups.setdefault(iid, []).append(rec)
        hash_to_items.setdefault(dlid, set()).add(iid)

    active_races = sum(1 for k in RACE if k.startswith(inst["kind"] + ":"))

    for iid, recs in groups.items():
        gkey = f"{inst['kind']}:{iid}"
        # candidate download hashes for this item, with live speeds
        cand = []
        for rec in recs:
            dlid = str(rec.get("downloadId") or "").lower()
            if not dlid:
                continue
            # skip season-pack style downloads (one torrent -> many episodes): monitor only
            if len(hash_to_items.get(dlid, set())) > 1:
                continue
            t = qbt.get(dlid)
            speed = (t.get("dlspeed") or 0) if t else 0
            progress = (t.get("progress") or 0) if t else (
                1 - (rec.get("sizeleft") or 0) / max(rec.get("size") or 1, 1))
            st = DL.setdefault(dlid, {"first_seen": now, "max_speed": 0, "kind": inst["kind"]})
            st["max_speed"] = max(st["max_speed"], speed)
            if not st.get("target_hit") and speed >= RACE_TARGET_MBPS * MB and dlid not in BASELINE_DL:
                st["target_hit"] = now
                m_observe("racearr_time_to_target_seconds", now - st["first_seen"], TTT_BUCKETS)
                m_inc("racearr_downloads_reached_target_total", {"instance": inst["kind"]})
            cand.append({"rec": rec, "hash": dlid, "t": t, "speed": speed,
                         "progress": progress, "age": now - st["first_seen"],
                         "max_speed": st["max_speed"], "baseline": dlid in BASELINE_DL})

        if not cand:
            continue
        # never manage the pre-existing backlog
        if all(c["baseline"] for c in cand):
            continue

        exclude = {c["hash"] for c in cand}
        winner = max(cand, key=lambda c: c["speed"])
        done = [c for c in cand if c["progress"] >= 0.999 or
                c["rec"].get("trackedDownloadState") in ("importPending", "imported", "importing")]

        # ---- if something already finished, cull the rest (they lost) ----
        if done:
            for c in cand:
                if c is not (done[0]) and c["hash"] != done[0]["hash"] and c["progress"] < 0.999:
                    kill_queue_record(inst, c["rec"], c["t"])
            RACE.pop(gkey, None)
            continue

        racing = gkey in RACE
        if not racing:
            # ---- SPEED SLA: trigger a race on a slow, non-baseline item ----
            oldest = max(c["age"] for c in cand)
            best_speed = max(c["max_speed"] for c in cand)
            in_cooldown = now < COOLDOWN.get(gkey, 0)
            if oldest >= SPEED_SLA_SECONDS and best_speed < SPEED_SLA_MBPS * MB and not in_cooldown:
                if active_races >= MAX_ACTIVE_RACES:
                    continue
                incident("speed_sla",
                         f"{inst['kind']} item {iid} at {best_speed/MB:.2f} MB/s after "
                         f"{int(oldest)}s (< {SPEED_SLA_MBPS} MB/s) — racing alternates",
                         item=iid, mbps=round(best_speed / MB, 3))
                slots = MAX_PER_ITEM - len(cand)
                grabbed = 0
                for r in candidate_releases(inst, iid, exclude):
                    if grabbed >= slots:
                        break
                    if grab_release(inst, r):
                        exclude.add(str(r.get("infoHash") or "").lower())
                        grabbed += 1
                with _lock:
                    STATS["races_started"] += 1
                    STATS["candidates_grabbed"] += grabbed
                m_inc("racearr_races_started_total", {"instance": inst["kind"]})
                if grabbed:
                    m_inc("racearr_candidates_grabbed_total", {"instance": inst["kind"]}, grabbed)
                    RACE[gkey] = {"start": now}
                    active_races += 1
                else:
                    # no better-seeded alternate exists — force a re-search and back off
                    # so we do not churn on genuinely scarce content
                    force_search(inst, iid)
                    COOLDOWN[gkey] = now + RACE_COOLDOWN_SECONDS
        else:
            # ---- CULL: keep the fastest, kill the losers; persist across loops so
            # late-arriving alternates (grab lag) are also culled until only the winner remains
            race_age = now - RACE[gkey]["start"]
            fastest = winner["speed"]
            have_winner = fastest >= RACE_TARGET_MBPS * MB
            timed_out = race_age >= RACE_MONITOR_SECONDS
            if race_age >= RACE_CULL_AFTER_SECONDS and (have_winner or timed_out):
                if timed_out and not have_winner:
                    incident("race_no_target",
                             f"{inst['kind']} item {iid}: no candidate reached "
                             f"{RACE_TARGET_MBPS} MB/s in {int(race_age)}s; keeping fastest "
                             f"{fastest/MB:.2f} MB/s", item=iid)
                for c in cand:
                    if c["hash"] != winner["hash"]:
                        kill_queue_record(inst, c["rec"], c["t"])
                if len(cand) <= 1 or timed_out:
                    m_observe("racearr_race_winner_mbps", fastest / MB, MBPS_BUCKETS)
                    m_inc("racearr_race_outcomes_total",
                          {"instance": inst["kind"],
                           "outcome": "won_target" if have_winner else "kept_below_target"})
                    RACE.pop(gkey, None)
                    COOLDOWN[gkey] = now + RACE_COOLDOWN_SECONDS

    # ---- PICKUP SLA: freshly-wanted items that never entered the queue ----
    queued_items = set(groups.keys())
    wanted = get_wanted_keys(inst)
    for gkey, title in wanted.items():
        if gkey in BASELINE_WANTED:
            continue
        iid = int(gkey.split(":", 1)[1])
        if iid in queued_items:
            ps = PICKUP.pop(gkey, None)
            if ps:
                lat = now - ps["first_seen"]
                m_observe("racearr_pickup_latency_seconds", lat, PICKUP_BUCKETS)
                m_inc("racearr_pickups_total",
                      {"instance": inst["kind"],
                       "result": "in_sla" if lat <= PICKUP_SLA_SECONDS else "breached"})
            continue
        ps = PICKUP.setdefault(gkey, {"first_seen": now, "alerted": False})
        if not ps["alerted"] and now - ps["first_seen"] >= PICKUP_SLA_SECONDS:
            incident("pickup_sla",
                     f"{inst['kind']} '{title}' wanted for {int(now - ps['first_seen'])}s "
                     f"with no grab (> {PICKUP_SLA_SECONDS}s) — forcing search", item=iid)
            force_search(inst, iid)
            ps["alerted"] = True


def prime_baseline():
    """Snapshot existing downloads + wanted so the pre-existing backlog is never managed."""
    global _primed
    qbt = qbit_by_hash()
    for inst in INSTANCES:
        try:
            for rec in get_queue(inst):
                dlid = str(rec.get("downloadId") or "").lower()
                if dlid:
                    BASELINE_DL.add(dlid)
            BASELINE_WANTED.update(get_wanted_keys(inst).keys())
        except Exception as e:  # noqa: BLE001
            log.warning("baseline prime failed (%s): %s", inst["kind"], e)
    _primed = True
    log.info("baseline primed: %d existing downloads, %d wanted items (protected)",
             len(BASELINE_DL), len(BASELINE_WANTED))


def loop():
    prime_baseline()
    while True:
        t0 = time.time()
        try:
            qbt = qbit_by_hash()
            for inst in INSTANCES:
                try:
                    process_instance(inst, qbt)
                except Exception as e:  # noqa: BLE001
                    log.exception("instance %s pass error: %s", inst["kind"], e)
        except Exception as e:  # noqa: BLE001
            log.exception("loop error: %s", e)
        with _lock:
            STATS["last_loop"] = time.time()
            STATS["loops"] += 1
        time.sleep(max(1, POLL_SECONDS - (time.time() - t0)))


# ----------------------------------------------------------------------------- health server
class _Health(BaseHTTPRequestHandler):
    def log_message(self, *a):  # silence access logs
        return

    def do_GET(self):
        if self.path.startswith("/healthz"):
            fresh = (time.time() - STATS["last_loop"]) < max(60, POLL_SECONDS * 4) or STATS["loops"] == 0
            self.send_response(200 if fresh else 503)
            self.end_headers()
            self.wfile.write(b"ok" if fresh else b"stale")
            return
        if self.path.startswith("/metrics"):
            body = render_metrics()
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; version=0.0.4; charset=utf-8")
            self.end_headers()
            self.wfile.write(body)
            return
        with _lock:
            body = json.dumps({**STATS, "dry_run": DRY_RUN, "instances": [i["kind"] for i in INSTANCES],
                               "active_races": len(RACE)}, default=str).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)


def main():
    if not INSTANCES:
        log.error("no instances configured (need RADARR_URL/RADARR_API_KEY and/or SONARR_*)")
        raise SystemExit(2)
    log.info("racearr starting | dry_run=%s | instances=%s | pickup<%ds speed>=%.1fMB/s@%ds "
             "target=%.1fMB/s max/item=%d protect_private=%s",
             DRY_RUN, [i["kind"] for i in INSTANCES], PICKUP_SLA_SECONDS, SPEED_SLA_MBPS,
             SPEED_SLA_SECONDS, RACE_TARGET_MBPS, MAX_PER_ITEM, PROTECT_PRIVATE)
    metrics_init()
    qbit_login()
    threading.Thread(target=loop, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", HEALTH_PORT), _Health).serve_forever()


if __name__ == "__main__":
    main()

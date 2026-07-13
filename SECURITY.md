# Security Policy

## Supported versions

racearr is released from `main` as immutable `:X.Y.Z` container images. Security
fixes land on the latest release; please run a current tag.

| Version | Supported |
|---|---|
| latest `:X.Y.Z` (and `:latest`) | :white_check_mark: |
| older tags | :warning: best effort |

## Reporting a vulnerability

Please report suspected vulnerabilities **privately**. Do not open a public issue
for anything exploitable.

- Preferred: open a private report via GitHub Security Advisories at
  <https://github.com/dragoshont/racearr/security/advisories/new>.
- Include a description, the affected version or commit, reproduction steps, and
  the impact.

You will get an acknowledgement, and a fix or mitigation will be coordinated
before any public disclosure. There is no bounty program; credit is given in the
advisory unless you prefer to stay anonymous.

## Scope and handling notes

racearr talks to your Radarr/Sonarr and qBittorrent using credentials you supply:

- **Secrets stay in your environment.** API keys and the qBittorrent password are
  read from environment variables (or your orchestrator's secret store) and are
  never written to racearr's database, logs, or race history. Do not commit them.
- **qBittorrent is read-only.** racearr only reads live torrent speed; every grab,
  removal, and search is a normal Radarr/Sonarr API call.
- **The web UI has no built-in auth.** Put it behind your reverse proxy or SSO, or
  keep it on a trusted network. The in-cluster `/metrics`, `/healthz`, `/status`,
  and Seerr webhook endpoints are unauthenticated by design; protect them at the
  network layer, and set `WEBHOOK_TOKEN` for the webhook.
- **Ships safe by default.** racearr starts in `DRY_RUN` (observe-only) until you
  explicitly arm it.

Please report anything that lets an attacker read those secrets, mutate the *arr
apps without authorization, or escape the documented network posture.

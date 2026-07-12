namespace Racearr.Core;

/// <summary>The two *arr instance kinds racearr manages. Both speak the stable API v3.</summary>
public enum ArrKind { Radarr, Sonarr }

/// <summary>
/// Descriptor for a configured *arr instance. Radarr and Sonarr differ only in a handful of
/// field/command names, so a single client + engine drive both (ADR-0001, "one ArrClient").
/// </summary>
public sealed record ArrInstance
{
    public required ArrKind Kind { get; init; }
    public required string Url { get; init; }
    public required string ApiKey { get; init; }

    /// <summary>Queue-record field carrying the *arr item id (<c>movieId</c> / <c>episodeId</c>).</summary>
    public string ItemField => Kind == ArrKind.Radarr ? "movieId" : "episodeId";
    /// <summary>Release-search query parameter.</summary>
    public string SearchParam => Kind == ArrKind.Radarr ? "movieId" : "episodeId";
    /// <summary>Command name for a forced search.</summary>
    public string SearchCommand => Kind == ArrKind.Radarr ? "MoviesSearch" : "EpisodeSearch";
    /// <summary>Command payload field holding the id list.</summary>
    public string SearchIdsField => Kind == ArrKind.Radarr ? "movieIds" : "episodeIds";
    /// <summary>Lowercase label used in metrics and race keys (<c>radarr</c> / <c>sonarr</c>).</summary>
    public string Name => Kind == ArrKind.Radarr ? "radarr" : "sonarr";

    /// <summary>Build the configured instances (an instance exists iff its API key is set).</summary>
    public static IReadOnlyList<ArrInstance> FromOptions(RacearrOptions o)
    {
        var list = new List<ArrInstance>();
        if (!string.IsNullOrWhiteSpace(o.RadarrApiKey))
            list.Add(new ArrInstance
            {
                Kind = ArrKind.Radarr,
                Url = (o.RadarrUrl ?? "http://localhost:7878").TrimEnd('/'),
                ApiKey = o.RadarrApiKey!,
            });
        if (!string.IsNullOrWhiteSpace(o.SonarrApiKey))
            list.Add(new ArrInstance
            {
                Kind = ArrKind.Sonarr,
                Url = (o.SonarrUrl ?? "http://localhost:8989").TrimEnd('/'),
                ApiKey = o.SonarrApiKey!,
            });
        return list;
    }
}

/// <summary>Domain projection of an *arr release-search result (the nested v3 JSON, flattened).</summary>
public sealed record Release
{
    public string Protocol { get; init; } = "";
    public int Seeders { get; init; }
    /// <summary>Vertical resolution (e.g. 1080, 2160); 0 when unknown.</summary>
    public int Resolution { get; init; }
    public string QualityName { get; init; } = "?";
    public bool Rejected { get; init; }
    public IReadOnlyList<string> Rejections { get; init; } = [];
    public string Indexer { get; init; } = "";
    public int? IndexerId { get; init; }
    public string InfoHash { get; init; } = "";
    public string Guid { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Total release size in bytes reported by the *arr search; 0 when unknown.</summary>
    public long Size { get; init; }
}

/// <summary>An active download record in an *arr queue.</summary>
public sealed record QueueRecord
{
    public int Id { get; init; }
    /// <summary>The *arr item id (movie/episode) this download belongs to.</summary>
    public int? ItemId { get; init; }
    /// <summary>Download-client hash (lowercased by the client) linking to the torrent.</summary>
    public string DownloadId { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Indexer reported by the *arr queue. Used only as a conservative fallback identity
    /// when an interactive-search result omits its info hash.</summary>
    public string Indexer { get; init; } = "";
    public long Size { get; init; }
    public long SizeLeft { get; init; }
    public string? TrackedDownloadState { get; init; }
    /// <summary>Overall tracked-download health: <c>ok</c> / <c>warning</c> / <c>error</c> (null when unknown).</summary>
    public string? TrackedDownloadStatus { get; init; }
}

/// <summary>Outcome of asking an *arr instance to grab one release.</summary>
public enum GrabOutcome
{
    Accepted,
    AlreadyPresent,
    Rejected,
    Failed,
    DryRun,
}

/// <summary>A classified grab result. Only <see cref="GrabOutcome.Accepted"/> starts a race.</summary>
public sealed record GrabResult(GrabOutcome Outcome, int? StatusCode = null);

/// <summary>Result of a command/delete mutation against an *arr instance.</summary>
public sealed record ArrMutationResult(bool Succeeded, int? StatusCode = null);

/// <summary>A qBittorrent snapshot whose availability is explicit; an unavailable snapshot must
/// never be interpreted as zero-speed torrents.</summary>
public sealed record TorrentSnapshot(bool Available, IReadOnlyDictionary<string, TorrentInfo> Items)
{
    public static TorrentSnapshot Unavailable { get; } = new(false, new Dictionary<string, TorrentInfo>());
}

/// <summary>A monitored-missing ("wanted") item, used for fresh-pickup detection.</summary>
public sealed record WantedItem(int Id, string Title);

/// <summary>Live torrent stats from qBittorrent (keyed elsewhere by lowercase infohash).</summary>
public sealed record TorrentInfo
{
    /// <summary>Download speed in bytes/second.</summary>
    public double DlSpeed { get; init; }
    /// <summary>Completion fraction, 0..1.</summary>
    public double Progress { get; init; }
    public string Tracker { get; init; } = "";
    public string MagnetUri { get; init; } = "";
    /// <summary>qBittorrent torrent state (e.g. downloading, stalledDL, metaDL); "" when unknown.</summary>
    public string State { get; init; } = "";
    /// <summary>Connected seeds; 0 when the torrent has no peers to pull the data from.</summary>
    public int NumSeeds { get; init; }
    /// <summary>Estimated seconds remaining (qBittorrent <c>eta</c>; 8640000 = unknown/∞).</summary>
    public long Eta { get; init; }
    /// <summary>Torrent (release) display name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Total size in bytes.</summary>
    public long Size { get; init; }
}

/// <summary>Library size for one *arr instance (movies for Radarr, series for Sonarr).</summary>
public sealed record LibraryStats(string Instance, int Total, int Downloaded);

/// <summary>
/// Whether an *arr instance is wired to refresh Plex when media lands: the "Plex Media Server"
/// connection with its import/upgrade events enabled. When it is missing or muted, Plex never
/// updates as new titles import — the dashboard surfaces this with a guided fix.
/// </summary>
public sealed record PlexLinkStatus(string Instance, bool Reachable, bool Configured, bool NotifiesOnImport, string? Detail)
{
    /// <summary>True only when Plex will actually be refreshed on new or upgraded imports.</summary>
    public bool Healthy => Reachable && Configured && NotifiesOnImport;
}

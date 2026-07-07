using System.Net.Http.Json;
using System.Text.Json;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Talks to a Radarr/Sonarr instance over the stable v3 API. Maps the nested v3 JSON into the
/// flat <see cref="Racearr.Core"/> domain records so the engine and decision logic stay clean.
/// Mutations (grab / delete-from-queue / forced search) flow only through here.
/// </summary>
public sealed class ArrClient(HttpClient http) : IArrClient
{
    private static HttpRequestMessage Build(HttpMethod method, ArrInstance inst, string pathAndQuery)
    {
        var req = new HttpRequestMessage(method, $"{inst.Url}/api/v3/{pathAndQuery}");
        req.Headers.Add("X-Api-Key", inst.ApiKey);
        return req;
    }

    private async Task<JsonElement> GetJsonAsync(ArrInstance inst, string pathAndQuery, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Build(HttpMethod.Get, inst, pathAndQuery), ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task<IReadOnlyList<QueueRecord>> GetQueueAsync(ArrInstance inst, CancellationToken ct)
    {
        var query = inst.Kind == ArrKind.Radarr
            ? "queue?page=1&pageSize=400&includeUnknownMovieItems=false"
            : "queue?page=1&pageSize=400";
        var root = await GetJsonAsync(inst, query, ct);
        var list = new List<QueueRecord>();
        if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in recs.EnumerateArray())
            {
                list.Add(new QueueRecord
                {
                    Id = GetInt(r, "id") ?? 0,
                    ItemId = GetInt(r, inst.ItemField),
                    DownloadId = (GetStr(r, "downloadId") ?? "").ToLowerInvariant(),
                    Title = GetStr(r, "title") ?? "",
                    Size = GetLong(r, "size") ?? 0,
                    SizeLeft = GetLong(r, "sizeleft") ?? 0,
                    TrackedDownloadState = GetStr(r, "trackedDownloadState"),
                });
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<WantedItem>> GetWantedMissingAsync(ArrInstance inst, CancellationToken ct)
    {
        var root = await GetJsonAsync(inst,
            "wanted/missing?page=1&pageSize=200&sortDirection=descending&sortKey=id&monitored=true", ct);
        var list = new List<WantedItem>();
        if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in recs.EnumerateArray())
            {
                var id = GetInt(r, "id");
                if (id is null) continue;
                var title = GetStr(r, "title")
                    ?? (r.TryGetProperty("movie", out var m) ? GetStr(m, "title") : null)
                    ?? "?";
                list.Add(new WantedItem(id.Value, title));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        var root = await GetJsonAsync(inst, $"release?{inst.SearchParam}={itemId}", ct);
        var list = new List<Release>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in root.EnumerateArray())
            {
                var rejections = new List<string>();
                if (r.TryGetProperty("rejections", out var rj) && rj.ValueKind == JsonValueKind.Array)
                    foreach (var x in rj.EnumerateArray())
                        rejections.Add(x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString());

                var res = 0;
                var qname = "?";
                if (r.TryGetProperty("quality", out var qroot) && qroot.TryGetProperty("quality", out var qq))
                {
                    res = GetInt(qq, "resolution") ?? 0;
                    qname = GetStr(qq, "name") ?? "?";
                }

                list.Add(new Release
                {
                    Protocol = GetStr(r, "protocol") ?? "",
                    Seeders = GetInt(r, "seeders") ?? 0,
                    Resolution = res,
                    QualityName = qname,
                    Rejected = GetBool(r, "rejected") ?? false,
                    Rejections = rejections,
                    Indexer = GetStr(r, "indexer") ?? "",
                    IndexerId = GetInt(r, "indexerId"),
                    InfoHash = GetStr(r, "infoHash") ?? "",
                    Guid = GetStr(r, "guid") ?? "",
                    Title = GetStr(r, "title") ?? "",
                });
            }
        }
        return list;
    }

    public async Task ForceSearchAsync(ArrInstance inst, int itemId, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["name"] = inst.SearchCommand, [inst.SearchIdsField] = new[] { itemId } };
        var req = Build(HttpMethod.Post, inst, "command");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> GrabAsync(ArrInstance inst, Release release, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["guid"] = release.Guid, ["indexerId"] = release.IndexerId };
        var req = Build(HttpMethod.Post, inst, "release");
        req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task DeleteQueueAsync(ArrInstance inst, int recordId, bool removeFromClient, bool blocklist, CancellationToken ct)
    {
        var query = $"queue/{recordId}?removeFromClient={(removeFromClient ? "true" : "false")}" +
                    $"&blocklist={(blocklist ? "true" : "false")}&skipRedownload=true";
        using var resp = await http.SendAsync(Build(HttpMethod.Delete, inst, query), ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static long? GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
    private static bool? GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

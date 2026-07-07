using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// SQLite-backed settings store. Seeds missing tunable knobs from environment-derived defaults on
/// first run; the Phase 3 UI reads/writes here. Uses a short-lived context per operation.
/// </summary>
public sealed class DbSettingsStore(IDbContextFactory<RacearrDbContext> factory) : ISettingsStore
{
    public IReadOnlyDictionary<string, string> SeedAndLoad(IReadOnlyDictionary<string, string> defaults)
    {
        using var db = factory.CreateDbContext();
        var existing = db.Settings.Select(s => s.Key).ToHashSet();
        foreach (var (key, value) in defaults)
            if (SettingKeys.Tunable.Contains(key) && !existing.Contains(key))
                db.Settings.Add(new Setting { Key = key, Value = value });
        db.SaveChanges();
        return LoadTunable(db);
    }

    public IReadOnlyDictionary<string, string> GetAll()
    {
        using var db = factory.CreateDbContext();
        return LoadTunable(db);
    }

    // Only ever surface tunable knobs: a stray non-tunable row (e.g. hand-inserted directly into the
    // database) is never returned or served as effective config.
    private static IReadOnlyDictionary<string, string> LoadTunable(RacearrDbContext db)
        => db.Settings.AsEnumerable()
            .Where(s => SettingKeys.Tunable.Contains(s.Key))
            .ToDictionary(s => s.Key, s => s.Value);

    public void Set(string key, string value)
    {
        // Only runtime-tunable knobs may be persisted; secrets and URLs stay environment-only so a
        // (future UI) write can never place a credential at rest in the database.
        if (!SettingKeys.Tunable.Contains(key))
            throw new ArgumentException($"'{key}' is not a runtime-tunable setting.", nameof(key));
        using var db = factory.CreateDbContext();
        var row = db.Settings.Find(key);
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }
}

/// <summary>
/// SQLite-backed history sink: one insert per event on a short-lived context. A database hiccup is
/// logged and swallowed so it can never crash the control loop (matching the Python "never let IO
/// crash the loop" posture).
/// </summary>
public sealed class DbEventSink(IDbContextFactory<RacearrDbContext> factory, ILogger<DbEventSink> log) : IEventSink
{
    public void Record(RaceEvent evt)
    {
        try
        {
            using var db = factory.CreateDbContext();
            db.RaceEvents.Add(evt);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "failed to persist race event {Kind}", evt.Kind);
        }
    }
}

/// <summary>SQLite-backed read side for the history UI (newest first, optional kind filter).</summary>
public sealed class DbEventHistory(IDbContextFactory<RacearrDbContext> factory) : IEventHistory
{
    public IReadOnlyList<RaceEvent> Recent(int limit, string? kind = null)
    {
        using var db = factory.CreateDbContext();
        IQueryable<RaceEvent> q = db.RaceEvents;
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(e => e.Kind == kind);
        // Newest first by event time (the indexed column), with the insertion id as a stable
        // tie-breaker so same-instant events keep a deterministic order.
        return q.OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id).Take(limit).ToList();
    }
}

/// <summary>
/// SQLite-backed connection store: seeds env-derived defaults on first run and upserts by kind.
/// Stores secrets (API key / password); callers must redact them before showing them in the UI/API.
/// </summary>
public sealed class DbConnectionStore(IDbContextFactory<RacearrDbContext> factory) : IConnectionStore
{
    public IReadOnlyList<Connection> GetAll()
    {
        using var db = factory.CreateDbContext();
        return db.Connections.OrderBy(c => c.Kind).ToList();
    }

    public Connection? Get(string kind)
    {
        using var db = factory.CreateDbContext();
        return db.Connections.FirstOrDefault(c => c.Kind == kind);
    }

    public int SeedMissing(IReadOnlyList<Connection> seeds)
    {
        using var db = factory.CreateDbContext();
        var present = db.Connections.Select(c => c.Kind).ToHashSet();
        var added = 0;
        foreach (var s in seeds)
            if (ConnectionKinds.IsValid(s.Kind) && !present.Contains(s.Kind))
            {
                db.Connections.Add(new Connection
                {
                    Kind = s.Kind, Url = s.Url, ApiKey = s.ApiKey,
                    Username = s.Username, Password = s.Password, Enabled = s.Enabled,
                });
                added++;
            }
        if (added > 0) db.SaveChanges();
        return added;
    }

    public void Save(Connection connection)
    {
        if (!ConnectionKinds.IsValid(connection.Kind))
            throw new ArgumentException($"'{connection.Kind}' is not a valid connection kind.", nameof(connection));
        using var db = factory.CreateDbContext();
        var row = db.Connections.FirstOrDefault(c => c.Kind == connection.Kind);
        if (row is null)
        {
            db.Connections.Add(connection);
        }
        else
        {
            row.Url = connection.Url;
            row.ApiKey = connection.ApiKey;
            row.Username = connection.Username;
            row.Password = connection.Password;
            row.Enabled = connection.Enabled;
        }
        db.SaveChanges();
    }
}

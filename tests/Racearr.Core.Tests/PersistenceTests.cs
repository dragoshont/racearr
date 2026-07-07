using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Racearr.Core;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Phase 2 persistence tests: the SQLite settings store and history sink. Each test runs against a
/// private in-memory database (a single kept-open connection) so the schema is real EF-Core SQLite
/// without touching disk or the network.
/// </summary>
public class PersistenceTests
{
    /// <summary>
    /// An <see cref="IDbContextFactory{T}"/> over one shared, kept-open <c>:memory:</c> connection.
    /// SQLite drops an in-memory database when its last connection closes, so the connection is held
    /// open for the lifetime of the test and the schema is created once up front.
    /// </summary>
    private sealed class InMemoryFactory : IDbContextFactory<RacearrDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public InMemoryFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }

        public RacearrDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<RacearrDbContext>().UseSqlite(_connection).Options);

        public void Dispose() => _connection.Dispose();
    }

    private static readonly IReadOnlyDictionary<string, string> Defaults =
        new Dictionary<string, string> { ["POLL_SECONDS"] = "5", ["DRY_RUN"] = "true" };

    [Fact]
    public void SeedAndLoad_PopulatesDefaults_AndIsIdempotent()
    {
        using var factory = new InMemoryFactory();
        var store = new DbSettingsStore(factory);

        var first = store.SeedAndLoad(Defaults);
        Assert.Equal("5", first["POLL_SECONDS"]);
        Assert.Equal("true", first["DRY_RUN"]);

        // Seeding again must not duplicate rows or change values.
        var second = store.SeedAndLoad(Defaults);
        Assert.Equal(first.Count, second.Count);

        using var db = factory.CreateDbContext();
        Assert.Equal(2, db.Settings.Count());
    }

    [Fact]
    public void SeedAndLoad_DoesNotOverwriteExistingValue()
    {
        using var factory = new InMemoryFactory();
        var store = new DbSettingsStore(factory);
        store.SeedAndLoad(Defaults);

        // A user (or the UI) changes a knob...
        store.Set("POLL_SECONDS", "30");

        // ...a later restart re-seeds from the environment defaults, which must NOT clobber the change.
        var reloaded = store.SeedAndLoad(Defaults);
        Assert.Equal("30", reloaded["POLL_SECONDS"]);
    }

    [Fact]
    public void Set_InsertsThenUpdates()
    {
        using var factory = new InMemoryFactory();
        var store = new DbSettingsStore(factory);

        store.Set("RACE_MIN_SEEDERS", "3");
        Assert.Equal("3", store.GetAll()["RACE_MIN_SEEDERS"]);

        store.Set("RACE_MIN_SEEDERS", "8");
        Assert.Equal("8", store.GetAll()["RACE_MIN_SEEDERS"]);

        using var db = factory.CreateDbContext();
        Assert.Equal(1, db.Settings.Count(s => s.Key == "RACE_MIN_SEEDERS"));
    }

    [Fact]
    public void EventSink_AppendsAndRoundtripsAllFields()
    {
        using var factory = new InMemoryFactory();
        var sink = new DbEventSink(factory, NullLogger<DbEventSink>.Instance);

        sink.Record(new RaceEvent
        {
            Kind = "race_outcome",
            Instance = "radarr",
            ItemId = 400,
            Outcome = "reached_target",
            Mbps = 12.5,
            Detail = "winner: fast1",
        });
        sink.Record(new RaceEvent { Kind = "kill", Instance = "sonarr", Detail = "loser" });

        using var db = factory.CreateDbContext();
        Assert.Equal(2, db.RaceEvents.Count());

        var outcome = db.RaceEvents.Single(e => e.Kind == "race_outcome");
        Assert.Equal("radarr", outcome.Instance);
        Assert.Equal(400, outcome.ItemId);
        Assert.Equal("reached_target", outcome.Outcome);
        Assert.Equal(12.5, outcome.Mbps);
        Assert.Equal("winner: fast1", outcome.Detail);
        Assert.True(outcome.TimestampUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Migration_CreatesSchema_OnAFreshDatabase()
    {
        // Applying the committed migration (not EnsureCreated) must yield a queryable schema, proving
        // the migration the container runs at startup is valid.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            using var db = new RacearrDbContext(
                new DbContextOptionsBuilder<RacearrDbContext>().UseSqlite(connection).Options);
            db.Database.Migrate();

            db.Settings.Add(new Setting { Key = "K", Value = "V" });
            db.RaceEvents.Add(new RaceEvent { Kind = "incident", Outcome = "speed_sla", Detail = "x" });
            db.SaveChanges();

            Assert.Equal("V", db.Settings.Single().Value);
            Assert.Equal("speed_sla", db.RaceEvents.Single().Outcome);
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public void Set_RejectsNonTunableKey()
    {
        using var factory = new InMemoryFactory();
        var store = new DbSettingsStore(factory);

        // A secret key must never be persistable through the settings store.
        Assert.Throws<ArgumentException>(() => store.Set("RADARR_API_KEY", "leaked"));

        using var db = factory.CreateDbContext();
        Assert.False(db.Settings.Any(s => s.Key == "RADARR_API_KEY"));
    }

    [Fact]
    public void SeedAndLoad_IgnoresNonTunableDefaults()
    {
        using var factory = new InMemoryFactory();
        var store = new DbSettingsStore(factory);

        var loaded = store.SeedAndLoad(new Dictionary<string, string>
        {
            ["POLL_SECONDS"] = "5",
            ["RADARR_API_KEY"] = "secret",     // must NOT be persisted
            ["RADARR_URL"] = "http://radarr",  // must NOT be persisted
        });

        Assert.True(loaded.ContainsKey("POLL_SECONDS"));
        Assert.False(loaded.ContainsKey("RADARR_API_KEY"));
        Assert.False(loaded.ContainsKey("RADARR_URL"));
    }

    [Fact]
    public void GetAll_OmitsPreExistingNonTunableRow()
    {
        using var factory = new InMemoryFactory();

        // A non-tunable row inserted directly into the DB (bypassing the store) must never surface.
        using (var seed = factory.CreateDbContext())
        {
            seed.Settings.Add(new Setting { Key = "RADARR_API_KEY", Value = "leaked" });
            seed.Settings.Add(new Setting { Key = "POLL_SECONDS", Value = "5" });
            seed.SaveChanges();
        }

        var store = new DbSettingsStore(factory);

        Assert.False(store.GetAll().ContainsKey("RADARR_API_KEY"));
        Assert.True(store.GetAll().ContainsKey("POLL_SECONDS"));
        Assert.False(store.SeedAndLoad(new Dictionary<string, string>()).ContainsKey("RADARR_API_KEY"));
    }
}

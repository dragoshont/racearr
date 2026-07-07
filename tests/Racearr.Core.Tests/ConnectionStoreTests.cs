using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Racearr.Core;
using Racearr.Web;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Phase-2 connection tests: the SQLite connection store (seed / upsert / validation) and the
/// effective-options resolution that Program.cs performs from the persisted connections. Runs
/// against a private in-memory SQLite database — no disk, no network.
/// </summary>
public class ConnectionStoreTests
{
    private sealed class InMemoryFactory : IDbContextFactory<RacearrDbContext>, IDisposable
    {
        private readonly SqliteConnection _c;
        public InMemoryFactory()
        {
            _c = new SqliteConnection("Data Source=:memory:");
            _c.Open();
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }
        public RacearrDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<RacearrDbContext>().UseSqlite(_c).Options);
        public void Dispose() => _c.Dispose();
    }

    [Fact]
    public void SeedMissing_inserts_only_absent_kinds_and_never_overwrites()
    {
        using var f = new InMemoryFactory();
        var store = new DbConnectionStore(f);

        var inserted = store.SeedMissing(new[]
        {
            new Connection { Kind = ConnectionKinds.Radarr, Url = "http://r", ApiKey = "rk" },
            new Connection { Kind = ConnectionKinds.Qbittorrent, Url = "http://q" },
        });
        Assert.Equal(2, inserted);

        // Re-seeding must not overwrite an existing row, even with different values.
        Assert.Equal(0, store.SeedMissing(new[] { new Connection { Kind = ConnectionKinds.Radarr, Url = "http://other", ApiKey = "x" } }));
        Assert.Equal("http://r", store.Get(ConnectionKinds.Radarr)!.Url);
        Assert.Null(store.Get(ConnectionKinds.Sonarr));
    }

    [Fact]
    public void Save_upserts_by_kind()
    {
        using var f = new InMemoryFactory();
        var store = new DbConnectionStore(f);
        store.Save(new Connection { Kind = ConnectionKinds.Sonarr, Url = "http://s1", ApiKey = "k1", Enabled = true });
        store.Save(new Connection { Kind = ConnectionKinds.Sonarr, Url = "http://s2", ApiKey = "k2", Enabled = false });

        Assert.Single(store.GetAll(), c => c.Kind == ConnectionKinds.Sonarr);
        var s = store.Get(ConnectionKinds.Sonarr)!;
        Assert.Equal("http://s2", s.Url);
        Assert.Equal("k2", s.ApiKey);
        Assert.False(s.Enabled);
    }

    [Fact]
    public void Save_rejects_unknown_kind()
    {
        using var f = new InMemoryFactory();
        var store = new DbConnectionStore(f);
        Assert.Throws<ArgumentException>(() => store.Save(new Connection { Kind = "plex", Url = "http://x" }));
    }

    [Fact]
    public void Effective_options_take_connection_values_and_a_disabled_arr_is_skipped()
    {
        // Mirrors the Program.cs resolver: an enabled arr surfaces its key; a disabled one surfaces "".
        var conn = new Dictionary<string, string?>
        {
            ["RADARR_URL"] = "http://radarr:7878",
            ["RADARR_API_KEY"] = "abc",
            ["SONARR_URL"] = "http://sonarr:8989",
            ["SONARR_API_KEY"] = "",            // disabled -> no key
            ["QBIT_URL"] = "http://qbit:8080",
        };
        var options = RacearrOptions.FromEnvironment(key => conn.TryGetValue(key, out var v) ? v : null);

        Assert.Equal("http://radarr:7878", options.RadarrUrl);
        Assert.Equal("abc", options.RadarrApiKey);
        Assert.Equal("http://qbit:8080", options.QbitUrl);
        Assert.True(options.HasAnyInstance);

        var instances = ArrInstance.FromOptions(options);
        Assert.Single(instances);               // only radarr; the disabled sonarr is skipped
        Assert.Equal(ArrKind.Radarr, instances[0].Kind);
    }
}

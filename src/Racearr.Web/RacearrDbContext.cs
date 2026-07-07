using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// The racearr SQLite database: persisted tunable <see cref="Setting"/>s and the append-only
/// <see cref="RaceEvent"/> history. A single file, mounted as a volume like the *arr apps.
/// </summary>
public sealed class RacearrDbContext(DbContextOptions<RacearrDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<RaceEvent> RaceEvents => Set<RaceEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
        });
        b.Entity<RaceEvent>(e =>
        {
            e.ToTable("race_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TimestampUtc);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Instance).HasMaxLength(16);
            e.Property(x => x.Outcome).HasMaxLength(48);
            e.Property(x => x.Detail).HasMaxLength(512);
        });
    }
}

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the context without running the app
/// host (which would <c>Environment.Exit(2)</c> when no *arr instance is configured).
/// </summary>
public sealed class RacearrDbContextFactory : IDesignTimeDbContextFactory<RacearrDbContext>
{
    public RacearrDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<RacearrDbContext>()
            .UseSqlite("Data Source=racearr-design.db")
            .Options);
}

using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Parity tests for the pure decision logic (ADR-0001). Each asserts the .NET behaviour matches
/// the Python <c>racearr.py</c> counterpart, including the tricky boundaries (raceable-rejection,
/// resolution cap, SLA edges) that the whole racing strategy depends on.
/// </summary>
public class RaceDecisionsTests
{
    private static Release Rel(int seeders = 10, int res = 1080, bool rejected = false,
        string[]? rejections = null, string protocol = "torrent", string indexer = "1337x",
        string infoHash = "") => new()
        {
            Protocol = protocol,
            Seeders = seeders,
            Resolution = res,
            Rejected = rejected,
            Rejections = rejections ?? [],
            Indexer = indexer,
            InfoHash = infoHash,
        };

    [Fact]
    public void RaceableRejection_NotRejected_IsRaceable()
        => Assert.True(RaceDecisions.IsRaceableRejection(Rel(rejected: false)));

    [Theory]
    [InlineData("Release already meets cutoff")]
    [InlineData("Not an upgrade for existing episode file(s)")]
    public void RaceableRejection_OnlyCutoffOrUpgrade_IsRaceable(string reason)
        => Assert.True(RaceDecisions.IsRaceableRejection(Rel(rejected: true, rejections: [reason])));

    [Theory]
    [InlineData("Quality not wanted")]
    [InlineData("Release rejected on size")]
    public void RaceableRejection_HardReason_NotRaceable(string reason)
        => Assert.False(RaceDecisions.IsRaceableRejection(Rel(rejected: true, rejections: [reason])));

    [Fact]
    public void RaceableRejection_RejectedButNoReasons_NotRaceable()
        => Assert.False(RaceDecisions.IsRaceableRejection(Rel(rejected: true, rejections: [])));

    [Fact]
    public void RaceableRejection_MixedCutoffAndHard_NotRaceable()
        => Assert.False(RaceDecisions.IsRaceableRejection(
            Rel(rejected: true, rejections: ["already meets cutoff", "size too big"])));

    [Fact]
    public void PrivateRelease_MatchesConfiguredIndexerCaseInsensitively()
    {
        var o = new RacearrOptions { PrivateIndexers = ["avistaz"] };
        Assert.True(RaceDecisions.IsPrivateRelease(Rel(indexer: "AvistaZ"), o));
        Assert.False(RaceDecisions.IsPrivateRelease(Rel(indexer: "1337x"), o));
    }

    [Fact]
    public void PrivateTorrent_NullIsNotPrivate()
        => Assert.False(RaceDecisions.IsPrivateTorrent(null, new RacearrOptions { PrivateTrackerDomains = ["avistaz"] }));

    [Fact]
    public void PrivateTorrent_MatchesTrackerOrMagnet()
    {
        var o = new RacearrOptions { PrivateTrackerDomains = ["avistaz"] };
        Assert.True(RaceDecisions.IsPrivateTorrent(new TorrentInfo { Tracker = "https://tracker.avistaz.to/announce" }, o));
        Assert.True(RaceDecisions.IsPrivateTorrent(new TorrentInfo { MagnetUri = "magnet:?tr=avistaz" }, o));
        Assert.False(RaceDecisions.IsPrivateTorrent(new TorrentInfo { Tracker = "https://tracker.example.com" }, o));
    }

    [Fact]
    public void SelectCandidates_FiltersThenSortsBySeedersDescending()
    {
        var o = new RacearrOptions(); // min seeders 3, max res 1080, protect private
        var releases = new[]
        {
            Rel(seeders: 50, res: 2160, infoHash: "uhd"),                 // over-res -> excluded
            Rel(seeders: 1, res: 1080, infoHash: "lowseed"),              // < min seeders -> excluded
            Rel(seeders: 20, res: 1080, infoHash: "a"),
            Rel(seeders: 40, res: 720, infoHash: "b"),
            Rel(seeders: 30, res: 0, infoHash: "unknownres"),             // unknown res allowed
            Rel(seeders: 99, protocol: "usenet", infoHash: "nzb"),        // non-torrent -> excluded
            Rel(seeders: 60, res: 1080, rejected: true,
                rejections: ["Quality not wanted"], infoHash: "hardrej"), // hard rejection -> excluded
        };
        var result = RaceDecisions.SelectCandidates(releases, new HashSet<string>(), o);
        Assert.Equal(["b", "unknownres", "a"], result.Select(r => r.InfoHash));
    }

    [Fact]
    public void SelectCandidates_ExcludesAlreadyGrabbedHashes()
    {
        var releases = new[] { Rel(seeders: 20, infoHash: "keepme"), Rel(seeders: 30, infoHash: "SKIP") };
        var result = RaceDecisions.SelectCandidates(releases, new HashSet<string> { "skip" }, new RacearrOptions());
        Assert.Equal(["keepme"], result.Select(r => r.InfoHash));
    }

    [Fact]
    public void SelectCandidates_ProtectPrivateExcludesPrivateIndexer()
    {
        var o = new RacearrOptions { PrivateIndexers = ["avistaz"] };
        var releases = new[]
        {
            Rel(seeders: 99, indexer: "AvistaZ", infoHash: "priv"),
            Rel(seeders: 10, indexer: "1337x", infoHash: "pub"),
        };
        var result = RaceDecisions.SelectCandidates(releases, new HashSet<string>(), o);
        Assert.Equal(["pub"], result.Select(r => r.InfoHash));
    }

    [Theory]
    [InlineData(100, "in_sla")]
    [InlineData(180, "in_sla")]     // boundary: latency == SLA is still in-SLA (<=)
    [InlineData(181, "breached")]
    public void ClassifyPickup_BoundaryAtSla(double latency, string expected)
        => Assert.Equal(expected, RaceDecisions.ClassifyPickup(latency, new RacearrOptions())); // pickup SLA 180

    [Fact]
    public void ShouldStartRace_RequiresAgedSlowAndNotInCooldown()
    {
        var o = new RacearrOptions(); // speed SLA 120s @ 1.0 MB/s
        var slow = 0.5 * RaceDecisions.MB;
        var fast = 2 * RaceDecisions.MB;
        Assert.True(RaceDecisions.ShouldStartRace(120, slow, false, o));   // aged, slow, free
        Assert.False(RaceDecisions.ShouldStartRace(119, slow, false, o));  // too young
        Assert.False(RaceDecisions.ShouldStartRace(200, fast, false, o));  // fast enough
        Assert.False(RaceDecisions.ShouldStartRace(200, slow, true, o));   // in cooldown
    }

    [Fact]
    public void RaceOutcome_Labels()
    {
        Assert.Equal("won_target", RaceDecisions.RaceOutcome(true));
        Assert.Equal("kept_below_target", RaceDecisions.RaceOutcome(false));
    }

    [Theory]
    [InlineData(120, 0.5, false, true)]    // aged + slow -> race
    [InlineData(120, 1.0, false, false)]   // speed exactly at threshold is NOT below it (Python uses <) -> no race
    [InlineData(120, 1.1, false, false)]   // fast enough -> no race
    public void ShouldStartRace_SpeedBoundaryIsStrictlyLessThan(double age, double mbps, bool cooldown, bool expected)
        => Assert.Equal(expected, RaceDecisions.ShouldStartRace(age, mbps * RaceDecisions.MB, cooldown, new RacearrOptions()));
}

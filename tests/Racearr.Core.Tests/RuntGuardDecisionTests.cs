using Racearr.Core;
using Xunit;

namespace Racearr.Core.Tests;

/// <summary>
/// Phase-A fake/dead-weight guard: the pure size-based runt classifier and the candidate size
/// floor. A "runt" is a torrent implausibly small to be the real media (a fake / sample / malware
/// payload) that only wins a race because tiny files finish fastest.
/// </summary>
public class RuntGuardDecisionTests
{
    private const long MB = 1024 * 1024;
    private static RacearrOptions Opt() => new() { RaceMinSizeMb = 50, RaceRuntRatio = 0.20, RaceMinSeeders = 3 };

    [Fact]
    public void IsRuntSize_UnknownSize_IsNeverARunt()
        => Assert.False(RaceDecisions.IsRuntSize(0, 2_000 * MB, Opt()));

    [Fact]
    public void IsRuntSize_BelowAbsoluteFloor_IsARunt()
        => Assert.True(RaceDecisions.IsRuntSize(5 * MB, 0, Opt())); // 5 MB < 50 MB floor, no peer needed

    [Fact]
    public void IsRuntSize_AboveFloor_NoPeers_IsNotARunt()
        => Assert.False(RaceDecisions.IsRuntSize(800 * MB, 0, Opt()));

    [Fact]
    public void IsRuntSize_TinyRelativeToLargestPeer_IsARunt()
        => Assert.True(RaceDecisions.IsRuntSize(300 * MB, 2_000 * MB, Opt())); // 300 < 20% of 2000 (=400)

    [Fact]
    public void IsRuntSize_ComparableToLargestPeer_IsNotARunt()
        => Assert.False(RaceDecisions.IsRuntSize(1_500 * MB, 2_000 * MB, Opt())); // 1500 > 400

    [Fact]
    public void IsRuntSize_FloorDisabled_IgnoresAbsolute()
        => Assert.False(RaceDecisions.IsRuntSize(5 * MB, 0, new RacearrOptions { RaceMinSizeMb = 0, RaceRuntRatio = 0.20 }));

    [Fact]
    public void IsRuntSize_RatioDisabled_IgnoresRelative()
        => Assert.False(RaceDecisions.IsRuntSize(300 * MB, 2_000 * MB, new RacearrOptions { RaceMinSizeMb = 50, RaceRuntRatio = 0 }));

    [Fact]
    public void SelectCandidates_DropsReleasesBelowTheSizeFloor_KeepsUnknownAndLarge()
    {
        var releases = new[]
        {
            new Release { Protocol = "torrent", Seeders = 10, Size = 2_000 * MB, InfoHash = "big" },
            new Release { Protocol = "torrent", Seeders = 20, Size = 5 * MB, InfoHash = "fake" },   // below floor -> dropped
            new Release { Protocol = "torrent", Seeders = 5, Size = 0, InfoHash = "unknown" },      // unknown -> kept
        };

        var picked = RaceDecisions.SelectCandidates(releases, new HashSet<string>(), Opt());

        Assert.DoesNotContain(picked, r => r.InfoHash == "fake");
        Assert.Contains(picked, r => r.InfoHash == "big");
        Assert.Contains(picked, r => r.InfoHash == "unknown");
        // Sorted by seeders descending among the kept releases (big=10 before unknown=5).
        Assert.Equal("big", picked[0].InfoHash);
    }

    [Theory]
    [InlineData("importBlocked", true)]
    [InlineData("importFailed", true)]
    [InlineData("failedPending", true)]
    [InlineData("importPending", false)]
    [InlineData("imported", false)]
    [InlineData("downloading", false)]
    [InlineData(null, false)]
    public void IsImportFailed_ClassifiesOnlyTerminalImportFailures(string? state, bool expected)
        => Assert.Equal(expected, RaceDecisions.IsImportFailed(new QueueRecord { TrackedDownloadState = state }));

    [Theory]
    [InlineData("stalledDL", 0, true)]
    [InlineData("metaDL", 0, true)]
    [InlineData("stalledDL", 3, false)]   // still has connected seeds -> may recover, leave to the normal grace
    [InlineData("downloading", 0, false)]
    [InlineData("", 0, false)]
    public void IsStalledDead_OnlyPeerlessStalledOrMetadata(string state, int numSeeds, bool expected)
        => Assert.Equal(expected, RaceDecisions.IsStalledDead(new TorrentInfo { State = state, NumSeeds = numSeeds }));

    [Fact]
    public void IsStalledDead_Null_IsFalse()
        => Assert.False(RaceDecisions.IsStalledDead(null));

    [Fact]
    public void ShouldStartRaceStalled_FiresOnlyWhenStalledPastFuseAndNotCoolingDown()
    {
        var o = new RacearrOptions { RaceStallSeconds = 45 };
        Assert.True(RaceDecisions.ShouldStartRaceStalled(60, anyStalledDead: true, inCooldown: false, o));
        Assert.False(RaceDecisions.ShouldStartRaceStalled(30, anyStalledDead: true, inCooldown: false, o));  // before the fuse
        Assert.False(RaceDecisions.ShouldStartRaceStalled(60, anyStalledDead: false, inCooldown: false, o)); // not stalled
        Assert.False(RaceDecisions.ShouldStartRaceStalled(60, anyStalledDead: true, inCooldown: true, o));   // cooling down
    }

    [Theory]
    [InlineData("stalledDL", 0, true)]    // stalled + peerless -> dead
    [InlineData("metaDL", 0, true)]
    [InlineData("downloading", 0, false)] // live -> not dead
    [InlineData("stalledDL", 5, false)]   // stalled but has seeds -> may recover, not dead
    public void IsDownloadDead_PresentTorrent_MatchesStalledDead(string state, int seeds, bool expected)
        => Assert.Equal(expected, RaceDecisions.IsDownloadDead(new TorrentInfo { State = state, NumSeeds = seeds }));

    [Fact]
    public void IsDownloadDead_MissingTorrent_IsDead() // orphaned: hash absent from the client snapshot
        => Assert.True(RaceDecisions.IsDownloadDead(null));

    [Fact]
    public void ShouldRemediatePack_FiresOnlyWhenDeadPastFuseAndNotCoolingDown()
    {
        var o = new RacearrOptions { RaceStallSeconds = 45 };
        Assert.True(RaceDecisions.ShouldRemediatePack(60, dead: true, inCooldown: false, o));
        Assert.False(RaceDecisions.ShouldRemediatePack(30, dead: true, inCooldown: false, o));  // before the fuse
        Assert.False(RaceDecisions.ShouldRemediatePack(60, dead: false, inCooldown: false, o)); // not dead
        Assert.False(RaceDecisions.ShouldRemediatePack(60, dead: true, inCooldown: true, o));   // cooling down
    }
}

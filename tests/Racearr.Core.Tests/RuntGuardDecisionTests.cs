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
}

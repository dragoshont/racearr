using Racearr.Core;

namespace Racearr.Core.Tests;

public class RacearrOptionsTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map) => k => map.GetValueOrDefault(k);

    [Fact]
    public void Defaults_match_the_python_contract()
    {
        var o = RacearrOptions.FromEnvironment(Env([]));

        Assert.Equal(12, o.PollSeconds);
        Assert.Equal(180, o.PickupSlaSeconds);
        Assert.Equal(120, o.SpeedSlaSeconds);
        Assert.Equal(1.0, o.SpeedSlaMbps);
        Assert.Equal(2.0, o.RaceTargetMbps);
        Assert.Equal(4, o.MaxConcurrentPerItem);
        Assert.Equal(1080, o.RaceMaxResolution);
        Assert.True(o.ProtectPrivate);
        Assert.True(o.DryRun);            // safe default: observe-only until explicitly armed
        Assert.Equal(9797, o.HealthPort);
        Assert.Empty(o.PrivateIndexers);
        Assert.False(o.HasAnyInstance);
    }

    [Fact]
    public void Reads_overrides_trims_urls_lowercases_lists_and_parses_bools()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["RADARR_URL"] = "http://radarr:7878/",
            ["RADARR_API_KEY"] = "abc",
            ["DRY_RUN"] = "false",
            ["SPEED_SLA_MBPS"] = "1.5",
            ["PRIVATE_INDEXERS"] = "AvistaZ, PassThePopcorn",
            ["QBIT_URL"] = "http://qbit:8080/",
        }));

        Assert.Equal("http://radarr:7878", o.RadarrUrl);   // trailing slash trimmed
        Assert.Equal("http://qbit:8080", o.QbitUrl);
        Assert.False(o.DryRun);
        Assert.Equal(1.5, o.SpeedSlaMbps);
        Assert.Equal(["avistaz", "passthepopcorn"], o.PrivateIndexers);  // lowercased + trimmed
        Assert.True(o.HasAnyInstance);
    }

    [Fact]
    public void DryRun_is_an_env_only_kill_switch_never_a_persisted_tunable()
    {
        // Regression guard (found live during cutover): a persisted DB DRY_RUN must never override
        // the deployment env. Keeping DRY_RUN out of the tunables makes it env-authoritative.
        Assert.DoesNotContain("DRY_RUN", SettingKeys.Tunable);
        Assert.DoesNotContain("DRY_RUN", new RacearrOptions().TunableSettings().Keys);
    }
}

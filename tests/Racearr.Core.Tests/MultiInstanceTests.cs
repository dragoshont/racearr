using System.Linq;
using Racearr.Core;

namespace Racearr.Core.Tests;

public class MultiInstanceTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map) => k => map.GetValueOrDefault(k);

    [Fact]
    public void Primary_instances_keep_their_historical_names()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["RADARR_URL"] = "http://radarr:7878", ["RADARR_API_KEY"] = "r",
            ["SONARR_URL"] = "http://sonarr:8989", ["SONARR_API_KEY"] = "s",
        }));

        var inst = ArrInstance.FromOptions(o);
        Assert.Equal(2, inst.Count);
        Assert.Equal("radarr", inst[0].Name);
        Assert.Equal("sonarr", inst[1].Name);
        Assert.Null(inst[0].Label);   // unchanged metric labels for single-instance setups
        Assert.Null(inst[1].Label);
    }

    [Fact]
    public void Extra_instances_are_parsed_and_named_uniquely()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["RADARR_URL"] = "http://radarr:7878", ["RADARR_API_KEY"] = "r",
            ["ARR_INSTANCES"] =
                "radarr|http://radarr-4k:7878/|k4k|4K;" +      // labeled  -> radarr-4k (slugged; trailing slash trimmed)
                "radarr|http://radarr-anime:7878|kan;" +       // unlabeled second radarr -> radarr-2
                "sonarr|http://sonarr-anime:8989|ksa|Anime",   // labeled  -> sonarr-anime
        }));

        Assert.Equal(3, o.ExtraArrInstances.Count);

        var inst = ArrInstance.FromOptions(o);
        var names = inst.Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "radarr", "radarr-4k", "radarr-2", "sonarr-anime" }, names);
        Assert.Equal("http://radarr-4k:7878", inst[1].Url);   // trailing slash trimmed
        Assert.Equal("radarr-4k", inst[1].Label);
        Assert.True(o.HasAnyInstance);
    }

    [Fact]
    public void Extra_instances_work_without_a_primary()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["ARR_INSTANCES"] = "radarr|http://only:7878|key",
        }));

        Assert.True(o.HasAnyInstance);
        var inst = ArrInstance.FromOptions(o);
        Assert.Equal("radarr", Assert.Single(inst).Name);   // first of its kind keeps the base name
    }

    [Fact]
    public void Malformed_extra_entries_are_skipped()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            // valid radarr ; too-few-fields ; bad kind ; valid sonarr
            ["ARR_INSTANCES"] = "radarr|http://ok:7878|key;garbage;plex|http://x|k;sonarr|http://s:8989|key",
        }));

        Assert.Equal(2, o.ExtraArrInstances.Count);
        var names = ArrInstance.FromOptions(o).Select(x => x.Name).ToArray();
        Assert.Equal(new[] { "radarr", "sonarr" }, names);
    }
}

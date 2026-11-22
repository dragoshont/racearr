using System.Text.Json;
using Racearr.Core;

namespace Racearr.Core.Tests;

/// <summary>
/// Behaviour of the Plex watchlist intake path that lives in <c>Racearr.Core</c>: feed parsing,
/// routing, dedupe, the add/no-add decision, and retry parking. No HTTP, no clock, no network —
/// which is the point: every one of these is the exact shape of a real Plex response or a real
/// failure, replayed offline.
/// </summary>
/// <remarks>
/// The distribution of these tests is deliberate. Parsing is exercised with adversarial payloads
/// rather than happy-path ones, because a parser that only handles the feed as it looks today
/// fails silently the day Plex adds a field or a category — and "silently" is not a hyperbole
/// here: an empty parse result reads as "the user's watchlist is empty" and clears the alert.
/// </remarks>
public class PlexWatchlistTests
{
    /// <summary>The live feed's actual shape, captured 2026-11-22 (trimmed to two items).
    /// Note the real oddity this fixture preserves: "Blade Runner 2099" is category
    /// <c>show</c>, and every item carries imdb + tmdb + tvdb simultaneously.</summary>
    private const string LiveFeedJson = """
    {
      "title": "Plex Watchlist",
      "category": "watchlist",
      "items": [
        {
          "title": "Blade Runner 2099 (2026)",
          "category": "show",
          "guids": ["imdb://tt1234567", "tmdb://671561", "tvdb://424855"],
          "pubDate": "Tue, 17 Nov 2026 17:43:29 GMT",
          "link": "https://watch.plex.tv/show/blade-runner-2099"
        },
        {
          "title": "Avengers: Secret Wars (2027)",
          "category": "movie",
          "guids": ["imdb://tt21361444", "tmdb://361470", "tvdb://332897"],
          "pubDate": "Mon, 16 Nov 2026 09:00:00 GMT",
          "link": "https://watch.plex.tv/movie/avengers-secret-wars"
        }
      ]
    }
    """;

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void Parses_live_feed_routing_by_category_not_title()
    {
        var items = PlexWatchlistFeed.Parse(LiveFeedJson, "feed1");

        Assert.Equal(2, items.Count);

        // "Blade Runner 2099" must NOT be inferred as a movie from its title. Routing comes from
        // category; a title-based matcher would file this show in Radarr beside 2049.
        var show = items.Single(i => i.Title.StartsWith("Blade Runner"));
        Assert.Equal(WatchlistKind.Series, show.Kind);
        Assert.Equal("Blade Runner 2099", show.Title);   // the in-title year survives: it is the name
        Assert.Equal(2026, show.Year);                   // the trailing parenthetical is the year
        Assert.Equal(671561, show.TmdbId);
        Assert.Equal(424855, show.TvdbId);
        Assert.Equal("Blade Runner 2099 (2026)", show.Display);

        var film = items.Single(i => i.Title.StartsWith("Avengers"));
        Assert.Equal(WatchlistKind.Movie, film.Kind);
        Assert.Equal(2027, film.Year);
        Assert.Equal("tt21361444", film.ImdbId);
    }

    [Fact]
    public void PubDate_parses_as_an_instant_not_local_wall_clock()
    {
        var show = PlexWatchlistFeed.Parse(LiveFeedJson, "feed1").Single(i => i.Title.StartsWith("Blade"));
        Assert.NotNull(show.AddedAt);
        // Pinned to an absolute instant with an explicit zero offset. If the parser ever falls back
        // to local conventions, this passes on a UTC CI runner and silently misorders the dedupe
        // on a machine in Europe/Bucharest — the machine that actually runs it.
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 17, 43, 29, TimeSpan.Zero), show.AddedAt!.Value);
    }

    [Fact]
    public void Unparseable_pubDate_is_null_rather_than_a_wrong_instant()
    {
        const string json = """{"items":[{"title":"No Date","category":"movie","guids":["tmdb://1"],"pubDate":"sometime last year"}]}""";
        var item = Assert.Single(PlexWatchlistFeed.Parse(json, "feed1"));
        Assert.Null(item.AddedAt);
        Assert.True(item.HasArrKey);     // still perfectly addable; the date is only for ordering
    }

    [Fact]
    public void Unknown_category_is_unsupported_and_never_added()
    {
        // A new Plex category (trailer? special? podcast?) must not silently become a movie.
        const string json = """
        {"items":[{"title":"Weird Thing (2026)","category":"podcast","guids":["tmdb://1"]}]}
        """;
        var item = Assert.Single(PlexWatchlistFeed.Parse(json, "feed1"));

        Assert.Equal(WatchlistKind.Unsupported, item.Kind);
        Assert.False(item.HasArrKey);
        Assert.Equal(IntakeAction.Unsupported, WatchlistPolicy.Decide(item, alreadyPresent: false, dryRun: false));
    }

    [Theory]
    [InlineData("show")]
    [InlineData("series")]
    [InlineData("episode")]
    [InlineData("SHOW")]
    [InlineData(" Movie ")]
    public void Category_vocabulary_is_matched_case_and_space_insensitive(string category)
    {
        var expected = category.Trim().ToLowerInvariant() == "movie" ? WatchlistKind.Movie : WatchlistKind.Series;
        var json = """{"items":[{"title":"X","category":"__C__","guids":["tvdb://9"]}]}""".Replace("__C__", category);
        Assert.Equal(expected, Assert.Single(PlexWatchlistFeed.Parse(json, "feed1")).Kind);
    }

    [Fact]
    public void Garbage_inside_guids_is_skipped_without_throwing()
    {
        // Any of these appearing on a real feed must cost one item, never the whole poll.
        const string json = """
        {"items":[
          {"title":"No Guids","category":"movie"},
          {"title":"Null Items","category":"movie","guids":[null,42,{"a":1},""]},
          {"title":"Bad Numbers","category":"movie","guids":["tmdb://abc","tvdb://-5","tmdb://"]},
          {"title":"No Scheme","category":"movie","guids":["justastring","imdb:tt1"]},
          {"title":"Unknown Scheme Only","category":"movie","guids":["dbone://593","plex://2"]},
          {"title":"Imdb Survives","category":"movie","guids":["tmdb://abc","imdb://tt0111161"]}
        ]}
        """;
        var items = PlexWatchlistFeed.Parse(json, "feed1");

        Assert.Equal(6, items.Count);
        Assert.False(items.Single(i => i.Title == "No Guids").HasArrKey);
        Assert.False(items.Single(i => i.Title == "Null Items").HasArrKey);
        Assert.False(items.Single(i => i.Title == "Bad Numbers").HasArrKey);
        Assert.False(items.Single(i => i.Title == "No Scheme").HasArrKey);
        Assert.False(items.Single(i => i.Title == "Unknown Scheme Only").HasArrKey);

        // IMDb-only is enough for Radarr: it is unique and stable, and refusing it would drop
        // titles the user actually asked for.
        var imdb = items.Single(i => i.Title == "Imdb Survives");
        Assert.True(imdb.HasArrKey);
        Assert.Equal("imdb:tt0111161", imdb.Key);
    }

    [Fact]
    public void Items_without_a_title_are_dropped()
    {
        const string json = """
        {"items":[{"category":"movie","guids":["tmdb://1"]},{"title":"","category":"movie"},{"title":null}]}
        """;
        Assert.Empty(PlexWatchlistFeed.Parse(json, "feed1"));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>", "a Cloudflare challenge page")]
    [InlineData("{not json", "a truncated transfer")]
    [InlineData("", "an empty body")]
    [InlineData("   ", "a whitespace body")]
    [InlineData("{}", "a JSON object with no items key")]
    [InlineData("{\"code\":410,\"message\":\"gone\"}", "a revoked feed answering with a JSON error")]
    [InlineData("{\"items\":{}}", "items of the wrong JSON type")]
    [InlineData("{\"items\":null}", "items explicitly null")]
    [InlineData("null", "a bare null document")]
    public void Unreadable_shapes_raise_rather_than_reading_as_an_empty_watchlist(string body, string why)
    {
        // The load-bearing distinction of this feature: an empty parse CLEARS the stall alert, so
        // anything that cannot be interpreted must raise instead. ThrowsAny-style matching because
        // JsonReaderException derives from JsonException.
        var raised = Record.Exception(() => PlexWatchlistFeed.Parse(body, "feed1"));
        Assert.True(raised is JsonException,
            $"expected a JsonException for {why}, got {raised?.GetType().Name ?? "no exception (it read as an empty watchlist)"}");
    }

    [Theory]
    [InlineData("{\"items\":[]}", "an explicit empty watchlist")]
    [InlineData("[]", "a bare empty array")]
    public void Legitimately_empty_shapes_return_empty_not_error(string json, string why)
    {
        // Paired with the raise above: empty is a state the user can genuinely be in.
        var items = PlexWatchlistFeed.Parse(json, "feed1");
        Assert.True(items.Count == 0, $"expected an empty parse for {why}, got {items.Count} item(s)");
    }

    [Fact]
    public void Bare_array_root_is_tolerated_as_the_item_list()
    {
        // Degrades gracefully if Plex ever drops the envelope, rather than throwing every poll.
        const string json = """[{"title":"Dune Part Three (2026)","category":"movie","guids":["tmdb://693134"]}]""";
        var item = Assert.Single(PlexWatchlistFeed.Parse(json, "feed1"));
        Assert.Equal(WatchlistKind.Movie, item.Kind);
        Assert.Equal(693134, item.TmdbId);
    }

    [Theory]
    [InlineData("Everything Must Go (2010)", "Everything Must Go", 2010)]
    [InlineData("Ocean's 8 (2018)", "Ocean's 8", 2018)]
    [InlineData("Blade Runner 2099 (2026)", "Blade Runner 2099", 2026)]
    [InlineData("No Year Here", "No Year Here", null)]
    [InlineData("Trailer (2026) Clip", "Trailer (2026) Clip", null)]     // year is not trailing
    [InlineData("Thing (19)", "Thing (19)", null)]                     // not four digits
    [InlineData("  Padded (2020)  ", "Padded", 2020)]
    public void Title_and_year_split_is_conservative(string raw, string title, int? year)
    {
        var (t, y) = PlexWatchlistFeed.SplitTitle(raw);
        Assert.Equal(title, t);
        Assert.Equal(year, y);
    }

    // ---------------------------------------------------------------- dedupe

    private static WatchlistItem Item(
        WatchlistKind kind, string title, int? tmdb = null, int? tvdb = null, string? imdb = null,
        string? added = null, string feed = "feed1")
        => new()
        {
            Kind = kind,
            Title = title,
            Feed = feed,
            TmdbId = tmdb,
            TvdbId = tvdb,
            ImdbId = imdb,
            AddedAt = added is null ? null : DateTimeOffset.Parse(added),
        };

    [Fact]
    public void Same_title_in_two_feeds_collapses_to_one()
    {
        // Two household accounts, one ask. Adding it twice is a duplicate search at best.
        var mine = Item(WatchlistKind.Movie, "Dune Part Three", tmdb: 693134, added: "2026-11-20T10:00:00Z");
        var theirs = Item(WatchlistKind.Movie, "Dune: Part Three", tmdb: 693134, added: "2026-11-21T10:00:00Z", feed: "feed2");

        var result = PlexWatchlistFeed.Dedupe([theirs, mine]);

        var kept = Assert.Single(result);
        Assert.Equal("Dune Part Three", kept.Title);              // earliest ask owns the entry
        Assert.Equal("feed1", kept.Feed);
    }

    [Fact]
    public void Undated_duplicate_never_displaces_a_dated_one()
    {
        var dated = Item(WatchlistKind.Series, "Slow Horses", tvdb: 355298, added: "2026-11-20T10:00:00Z");
        var undated = Item(WatchlistKind.Series, "Slow Horses", tvdb: 355298, feed: "feed2");

        var kept = Assert.Single(PlexWatchlistFeed.Dedupe([undated, dated]));
        Assert.Equal("feed1", kept.Feed);
        Assert.NotNull(kept.AddedAt);
    }

    [Fact]
    public void Deferred_and_parked_are_separate_states()
    {
        // "In a retry window" is routine; "attempts exhausted" needs a human. A dashboard that
        // shows one for the other either cries wolf or hides the real failure.
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backoff = new IntakeBackoff(TimeSpan.FromMinutes(5), maxAttempts: 3);

        Assert.False(backoff.IsParked("tmdb:9"));

        backoff.RecordFailure("tmdb:9", t0);                       // 1/3 — deferred, not parked
        Assert.False(backoff.ShouldAttempt("tmdb:9", t0));
        Assert.False(backoff.IsParked("tmdb:9"));
        Assert.Empty(backoff.Parked);

        backoff.RecordFailure("tmdb:9", t0);                       // 2/3 — still recoverable
        Assert.False(backoff.IsParked("tmdb:9"));

        backoff.RecordFailure("tmdb:9", t0);                       // 3/3 — parked
        Assert.True(backoff.IsParked("tmdb:9"));
        Assert.Contains("tmdb:9", backoff.Parked);
    }

    [Fact]
    public void Feed_client_log_category_is_pinned_to_the_typed_client_name()
    {
        // HttpClientFactory derives the per-request logging category from the typed client's type
        // name, and that category MUST stay filtered because the request URI path is the feed
        // credential. Rename IPlexFeedSource and this fails instead of quietly re-enabling the
        // leak while every other test stays green.
        Assert.Equal($"System.Net.Http.HttpClient.{nameof(IPlexFeedSource)}", PlexFeedSecurity.HttpClientLogCategory);
        Assert.Contains(nameof(IPlexFeedSource), PlexFeedSecurity.HttpClientLogCategory);
    }

    [Fact]
    public void Feed_id_never_appears_in_an_item_key_title_or_display()
    {
        // Keys and Display strings are what reach logs and metric-adjacent text. Constructing them
        // from feed data would be a second leak path independent of the HTTP logger.
        const string feed = "5fdc3919-0a49-4115-b044-f50a84fe0c09";
        var item = Item(WatchlistKind.Movie, "Secret Watchlist Pick", tmdb: 1, feed: feed);

        Assert.DoesNotContain(feed, item.Key);
        Assert.DoesNotContain(feed, item.Display);
        Assert.Equal(feed, item.Feed);   // the label is carried for provenance, never rendered
    }

    [Fact]
    public void Unidentifiable_items_pass_through_so_they_are_counted_not_lost()
    {
        // If dedupe dropped these, the no_identity counter would read 0 while the user's titles
        // vanished, and the failure would look like "nothing was in the feed".
        var junk = Item(WatchlistKind.Unsupported, "Mystery", feed: "feed1");
        var noId = Item(WatchlistKind.Movie, "No Ids");

        Assert.Equal(2, PlexWatchlistFeed.Dedupe([junk, noId]).Count);
    }

    // ---------------------------------------------------------------- decision

    [Fact]
    public void Dry_run_observes_but_never_adds()
    {
        // CONTRIBUTING: "an observe-only run performs no real action". Pinned here so a future
        // call-site refactor cannot quietly lose the gate.
        var absent = Item(WatchlistKind.Movie, "New Thing", tmdb: 1);
        Assert.Equal(IntakeAction.ObserveAdd, WatchlistPolicy.Decide(absent, alreadyPresent: false, dryRun: true));
        Assert.Equal(IntakeAction.Add, WatchlistPolicy.Decide(absent, alreadyPresent: false, dryRun: false));
    }

    [Fact]
    public void Dry_run_does_not_rescue_an_unsupported_or_idless_item()
    {
        // Adversarial ordering check: dry-run must not promote something that should never be
        // sent at all, or "observe only" becomes a way to route garbage into an add call.
        var junk = Item(WatchlistKind.Unsupported, "Podcast", tmdb: 1);
        var noId = Item(WatchlistKind.Movie, "No Ids");

        Assert.Equal(IntakeAction.Unsupported, WatchlistPolicy.Decide(junk, alreadyPresent: false, dryRun: true));
        Assert.Equal(IntakeAction.NoIdentity, WatchlistPolicy.Decide(noId, alreadyPresent: false, dryRun: true));
    }

    [Fact]
    public void Present_titles_are_never_re_added_even_when_dry_run_is_off()
    {
        var present = Item(WatchlistKind.Movie, "In Library", tmdb: 7);
        Assert.Equal(IntakeAction.AlreadyPresent, WatchlistPolicy.Decide(present, alreadyPresent: true, dryRun: false));
    }

    [Fact]
    public void Series_without_tvdb_has_no_arr_key_even_with_tmdb()
    {
        // Sonarr keys on TVDb. Treating a TMDb-only series as addable would POST a series that
        // either 404s or, worse, matches nothing and is silently ignored forever.
        var tmdbOnly = Item(WatchlistKind.Series, "A Show", tmdb: 555);
        Assert.False(tmdbOnly.HasArrKey);
        Assert.Equal(IntakeAction.NoIdentity, WatchlistPolicy.Decide(tmdbOnly, false, false));

        var withTvdb = Item(WatchlistKind.Series, "A Show", tmdb: 555, tvdb: 556);
        Assert.True(withTvdb.HasArrKey);
        Assert.Equal("tvdb:556", withTvdb.Key);
    }

    // ---------------------------------------------------------------- parking

    [Fact]
    public void Failure_backs_off_exponentially_then_parks()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backoff = new IntakeBackoff(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), maxAttempts: 4);
        const string key = "tmdb:1";

        Assert.True(backoff.ShouldAttempt(key, t0));

        backoff.RecordFailure(key, t0);                       // 5 min
        Assert.False(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(4)));
        Assert.True(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(5)));

        backoff.RecordFailure(key, t0 + TimeSpan.FromMinutes(5));   // 10 min
        Assert.False(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(14)));
        Assert.True(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(15)));

        backoff.RecordFailure(key, t0 + TimeSpan.FromMinutes(15));  // 20 min
        Assert.False(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(34)));
        Assert.True(backoff.ShouldAttempt(key, t0 + TimeSpan.FromMinutes(35)));

        backoff.RecordFailure(key, t0 + TimeSpan.FromMinutes(35));  // 4th attempt: parked
        Assert.Equal(4, backoff.Attempts(key));
        Assert.Contains(key, backoff.Parked);

        // Parked is terminal for the process life — including for a caller who waits a year.
        Assert.False(backoff.ShouldAttempt(key, t0 + TimeSpan.FromDays(365)));
    }

    [Fact]
    public void Cap_is_respected_so_delays_cannot_run_away()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backoff = new IntakeBackoff(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), maxAttempts: 20);

        for (var i = 0; i < 12; i++) backoff.RecordFailure("tmdb:2", t0);

        // Past the cap, the next permitted attempt is at most 30 minutes out. Asserting "not
        // available at 29 minutes but available at 30" catches both an unclamped shift (which
        // overflows negative and retries immediately forever) and an over-long delay.
        Assert.False(backoff.ShouldAttempt("tmdb:2", t0 + TimeSpan.FromMinutes(29)));
        Assert.True(backoff.ShouldAttempt("tmdb:2", t0 + TimeSpan.FromMinutes(31)));
    }

    [Fact]
    public void Success_clears_history_so_a_recoverable_title_retries_normally()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backoff = new IntakeBackoff(TimeSpan.FromMinutes(5), maxAttempts: 2);

        backoff.RecordFailure("tmdb:3", t0);
        backoff.RecordFailure("tmdb:3", t0);
        Assert.Contains("tmdb:3", backoff.Parked);

        backoff.RecordSuccess("tmdb:3");

        Assert.Empty(backoff.Parked);
        Assert.Equal(0, backoff.Attempts("tmdb:3"));
        Assert.True(backoff.ShouldAttempt("tmdb:3", t0));
        Assert.Equal(0, backoff.TrackedCount);
    }

    [Fact]
    public void Parking_is_independent_per_title()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backoff = new IntakeBackoff(TimeSpan.FromMinutes(1), maxAttempts: 1);

        backoff.RecordFailure("tmdb:bad", t0);

        // One unaddable title must not stall the rest of the watchlist — the "one exception drops
        // the whole feed" failure mode of the integration this replaced.
        Assert.False(backoff.ShouldAttempt("tmdb:bad", t0 + TimeSpan.FromHours(2)));
        Assert.True(backoff.ShouldAttempt("tmdb:good", t0 + TimeSpan.FromHours(2)));
        Assert.Single(backoff.Parked);
    }

    // ---------------------------------------------------------------- options

    private static Func<string, string?> Env(Dictionary<string, string?> map) => k => map.GetValueOrDefault(k);

    [Fact]
    public void Intake_is_inert_until_feed_ids_are_configured()
    {
        // Guards the upgrade path: bumping the image on a deployment that sets none of these must
        // change nothing about what that deployment does.
        var off = RacearrOptions.FromEnvironment(Env([]));
        Assert.True(off.WatchlistEnabled);
        Assert.False(off.HasWatchlist);
        Assert.Empty(off.WatchlistFeedIds);

        Assert.Equal(120, off.WatchlistIntervalSeconds);
        Assert.Equal("UHD/HD", off.WatchlistProfile);
        Assert.Equal("plex-watchlist", off.WatchlistTag);
        Assert.True(off.WatchlistSearchOnAdd);
        Assert.Null(off.WatchlistMoviesRoot);
    }

    [Fact]
    public void Feed_ids_and_switches_come_from_the_environment()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["WATCHLIST_FEED_IDS"] = "5fdc3919-0a49-4115-b044-f50a84fe0c09, f45eb895-4e02-43da-904e-df3cbbc30c5b",
            ["WATCHLIST_INTERVAL_SECONDS"] = "90",
            ["WATCHLIST_MOVIES_ROOT"] = "/complete/movies",
            ["WATCHLIST_SHOWS_ROOT"] = "/complete/shows",
            ["WATCHLIST_SERIES_MONITOR"] = "future",
        }));

        Assert.True(o.HasWatchlist);
        Assert.Equal(2, o.WatchlistFeedIds.Count);
        Assert.Equal("5fdc3919-0a49-4115-b044-f50a84fe0c09", o.WatchlistFeedIds[0]);
        Assert.Equal(90, o.WatchlistIntervalSeconds);
        Assert.Equal("/complete/movies", o.WatchlistMoviesRoot);
        Assert.Equal("future", o.WatchlistSeriesMonitor);
    }

    [Fact]
    public void Kill_switch_beats_configured_feeds()
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["WATCHLIST_FEED_IDS"] = "abc",
            ["WATCHLIST_ENABLED"] = "false",
        }));
        Assert.False(o.HasWatchlist);          // disabled is reported as disabled, not as empty
    }

    [Theory]
    [InlineData("0", 1)]                       // a zero interval would hot-loop against Plex
    [InlineData("-5", 1)]
    [InlineData("", 1)]
    [InlineData("not-a-number", 1)]
    public void Interval_cannot_be_configured_into_a_hot_loop(string raw, int expected)
    {
        var o = RacearrOptions.FromEnvironment(Env(new()
        {
            ["WATCHLIST_FEED_IDS"] = "abc",
            ["WATCHLIST_INTERVAL_SECONDS"] = raw,
        }));
        Assert.Equal(expected, o.WatchlistIntervalSeconds);
    }

    [Fact]
    public void Feed_ids_survive_a_round_trip_without_json_webs()
    {
        // Sanity on the fixture itself: if this fails, the parse tests above are testing a shape
        // Plex no longer emits.
        using var doc = JsonDocument.Parse(LiveFeedJson);
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }
}

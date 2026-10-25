using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Tests;

/// <summary>
/// The flight tuning: what a session measured turns into setting changes with their reasons, applied only for the
/// preset flown, remembered per preset, and undone by Reset.
/// </summary>
public sealed class TuningTests : IDisposable
{
    private const long Gb = 1L << 30;
    private static readonly DateTime When = new(2026, 10, 26, 18, 0, 0);

    /// <summary>Sharp, as the window sets it, with the connection at 160 downloads.</summary>
    private static readonly CurrentSettings Sharp = new(-1, Streaming: true, NearWait: 30, MaxWait: 2, BuildMemoryMb: 512, BuildTiles: 32768,
        DownloadWorkers: 160, PagingK: null);

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A healthy 30 minutes: nearly every read real, quick downloads, memory to spare, k as expected.</summary>
    private static StreamingFacts Streaming(long flightWaits = 100, long flightTimedOut = 5, IReadOnlyList<double>? flightNeeded = null,
        long startWaits = 50, long startTimedOut = 0, IReadOnlyList<double>? startNeeded = null, (double, int)? measuredK = null) =>
        new(10_000, 9_900, 100, 0, startWaits, startTimedOut, startNeeded ?? [.. Enumerable.Repeat(4.0, 50)],
            flightWaits, flightTimedOut, flightNeeded ?? [.. Enumerable.Repeat(0.5, 100)], 7650, measuredK ?? (7700, 500));

    private static FlightFacts Flight(StreamingFacts? streaming = null, double minutes = 30) => new()
    {
        MinutesRunning = minutes + 5,
        MinutesInFlight = minutes,
        Streaming = streaming ?? Streaming(),
        Tiles = 400_000,
        Attempts = 400_000,
        MeanTileSeconds = 0.3,
        DownloadsQueuedShare = 0.1,
        TotalMemoryBytes = 32 * Gb,
        MinAvailableMemoryBytes = 10 * Gb,
    };

    private static Suggestion Only(FlightReview review, string key) => Assert.Single(review.Suggestions, s => s.Key == key);

    [Fact]
    public void A_flight_that_went_well_needs_no_changes()
    {
        FlightReview review = Tuner.Review(Flight(), Sharp, When);
        Assert.False(review.TooShort);
        Assert.Empty(review.Suggestions);
    }

    [Fact]
    public void Under_ten_minutes_in_flight_is_too_short_to_judge()
    {
        FlightReview review = Tuner.Review(Flight(Streaming(flightTimedOut: 90), minutes: 9), Sharp, When);
        Assert.True(review.TooShort);
        Assert.Empty(review.Suggestions);
    }

    [Fact]
    public void Windows_short_of_memory_halves_the_build_memory_and_never_raises_it()
    {
        FlightFacts facts = Flight(Streaming(flightTimedOut: 60)) with { MinAvailableMemoryBytes = Gb, BuildsWaitedForMemoryShare = 0.8 };

        Suggestion s = Only(Tuner.Review(facts, Sharp, When), "build-memory");

        Assert.Equal(("512", "256"), (s.From, s.To));
        Assert.Contains("1.1 GB", s.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_short_of_memory_while_OrthoStreamXP_held_far_more_than_its_builds_keeps_the_build_memory()
    {
        // 512 MB of builds, yet 8 GB held: halving the builds would free little and only slow the sharpening.
        FlightFacts facts = Flight() with { MinAvailableMemoryBytes = Gb, OwnMemoryAtMinBytes = 8 * Gb };

        FlightReview review = Tuner.Review(facts, Sharp, When);

        Assert.DoesNotContain(review.Suggestions, s => s.Key == "build-memory");
        Suggestion finding = Assert.Single(review.Suggestions);
        Assert.Null(finding.Key);
        Assert.Contains("8.6 GB", finding.Why, StringComparison.Ordinal); // 8 GiB in the window's GB

        // Within what the builds and the rest may hold, the builds are still what to cut.
        Assert.Equal("256", Only(Tuner.Review(facts with { OwnMemoryAtMinBytes = 3 * Gb }, Sharp, When), "build-memory").To);
    }

    [Fact]
    public void Downloads_timing_out_or_refused_mean_fewer_at_once()
    {
        FlightFacts facts = Flight() with { TroubledAttempts = 20_000 }; // 5%
        Suggestion s = Only(Tuner.Review(facts, Sharp, When), "download-workers");
        Assert.Equal(("160", "128"), (s.From, s.To));
    }

    [Fact]
    public void Downloads_all_busy_while_tiles_still_arrive_fast_mean_more_at_once()
    {
        FlightFacts facts = Flight() with { DownloadsQueuedShare = 0.6, MeanTileSeconds = 0.3 };
        Assert.Equal("208", Only(Tuner.Review(facts, Sharp, When), "download-workers").To);

        // Slow tiles: the line is full already, more at once would only wait longer.
        Assert.Empty(Tuner.Review(facts with { MeanTileSeconds = 1.2 }, Sharp, When).Suggestions);
    }

    /// <summary>A connection test that found 256 at once fastest, at about 260 Mbit/s.</summary>
    private static ConnectionResult Tested(int suggested = 256, double mbits = 260) =>
        new(new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc), "bing", [new ConnectionRound(suggested, 1024, 0, 16_900_000, 0.7, 0.14, mbits)], suggested);

    [Fact]
    public void With_a_connection_test_more_at_once_goes_straight_to_the_tested_number()
    {
        FlightFacts facts = Flight() with { DownloadsQueuedShare = 0.6, MeanTileSeconds = 0.3 };
        Suggestion s = Only(Tuner.Review(facts, Sharp, When, Tested()), "download-workers");

        Assert.Equal(("160", "256"), (s.From, s.To)); // not 208, a step at a time
        Assert.Contains("connection test", s.Why, StringComparison.Ordinal);
        Assert.Contains("256 at once", s.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_that_queues_at_the_tested_number_asks_for_the_test_again()
    {
        FlightFacts facts = Flight() with { DownloadsQueuedShare = 0.6, MeanTileSeconds = 0.3 };
        FlightReview review = Tuner.Review(facts, Sharp, When, Tested(suggested: 128, mbits: 140), peakMbits: 50);

        Assert.DoesNotContain(review.Suggestions, s => s.Key == "download-workers");
        Suggestion again = Assert.Single(review.Suggestions, s => s.Key is null);
        Assert.Equal("Test the connection again", again.Setting);
        Assert.Contains("peaked at 50 Mbit/s", again.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusals_at_the_tested_number_still_mean_fewer_and_say_the_servers_were_busier()
    {
        FlightFacts facts = Flight() with { TroubledAttempts = 20_000 };
        Suggestion s = Only(Tuner.Review(facts, Sharp with { DownloadWorkers = 256 }, When, Tested()), "download-workers");

        Assert.Equal(("256", "192"), (s.From, s.To));
        Assert.Contains("busier during this flight", s.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_without_queuing_or_errors_changes_nothing_whatever_the_test()
    {
        Assert.DoesNotContain(Tuner.Review(Flight(), Sharp, When, Tested()).Suggestions, s => s.Key == "download-workers" || s.Key is null);
    }

    [Fact]
    public void Stand_ins_in_flight_whose_pictures_came_soon_after_lengthen_max_wait()
    {
        // 200 waits, 80 stand-ins; the pictures took 0.04-4 s: most within 3.2 s.
        StreamingFacts streaming = Streaming(flightWaits: 200, flightTimedOut: 80, flightNeeded: [.. Enumerable.Range(1, 100).Select(i => i * 0.04)]);

        Suggestion s = Only(Tuner.Review(Flight(streaming), Sharp, When), "max-wait");

        Assert.Equal(("2", "3.5"), (s.From, s.To));
    }

    [Fact]
    public void Pictures_far_too_slow_for_any_wait_are_a_finding_not_a_change()
    {
        StreamingFacts streaming = Streaming(flightWaits: 200, flightTimedOut: 150, flightNeeded: [.. Enumerable.Repeat(20.0, 150)]);

        FlightReview review = Tuner.Review(Flight(streaming), Sharp, When);

        Suggestion finding = Assert.Single(review.Suggestions);
        Assert.Null(finding.Key);
        Assert.False(review.HasChanges);
        Assert.Contains("lower quality than Sharp", finding.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_ending_with_blurry_textures_next_to_the_aircraft_lengthens_near_wait()
    {
        // 40 waits at the start, 10 still blurry after 30 s; those arrived after 32-50 s.
        double[] needed = [.. Enumerable.Repeat(5.0, 30), .. Enumerable.Range(0, 10).Select(i => 32.0 + 2 * i)];
        StreamingFacts streaming = Streaming(startWaits: 40, startTimedOut: 10, startNeeded: needed);

        Suggestion s = Only(Tuner.Review(Flight(streaming), Sharp, When), "near-wait");

        Assert.Equal(("30", "45"), (s.From, s.To));
    }

    [Fact]
    public void A_paging_k_measured_well_off_the_model_is_suggested()
    {
        StreamingFacts streaming = Streaming(measuredK: (9_820, 300));

        Suggestion s = Only(Tuner.Review(Flight(streaming), Sharp, When), "paging-k");

        Assert.Equal(("auto", "9800"), (s.From, s.To));
        Assert.Empty(Tuner.Review(Flight(Streaming(measuredK: (9_820, 40))), Sharp, When).Suggestions); // too few changes to go on
    }

    [Fact]
    public void Builds_held_back_by_their_budget_while_the_line_had_room_get_a_bigger_one()
    {
        FlightFacts blurry = Flight(Streaming(flightTimedOut: 30));

        Assert.Equal("768", Only(Tuner.Review(blurry with { BuildsWaitedForMemoryShare = 0.5 }, Sharp, When), "build-memory").To);
        Assert.Equal("49152", Only(Tuner.Review(blurry with { BuildsWaitedForTilesShare = 0.5 }, Sharp, When), "build-tiles").To);

        // The line full: a bigger budget would only queue more downloads.
        Assert.Empty(Tuner.Review(blurry with { BuildsWaitedForTilesShare = 0.5, DownloadsQueuedShare = 0.4 }, Sharp, When).Suggestions);
    }

    // ---------------------------------------------------------------- the settings

    private SettingsDocument Settings(QualityPreset preset)
    {
        SettingsDocument settings = SettingsDocument.CreateNew(_tree.PathOf("orthostreamxp.ini"), @"C:\X-Plane 12", @"C:\Packages", @"D:\cache", preset);
        settings.Set("download-workers", 160);
        return settings;
    }

    private TunedStore Store() => TunedStore.Load(_tree.PathOf("orthostreamxp.ini"));

    private static FlightReview ReviewOf(int level, params Suggestion[] suggestions) => new(level, When, 30, suggestions);

    [Fact]
    public void Every_preset_sets_every_setting_the_window_hides()
    {
        string[] hidden =
        [
            "dds-build-workers", "encoder-workers", "filesystem-threads", "mip-streaming", "near-wait", "max-wait", "build-memory", "build-tiles",
            "prefetch-loaded-scenery", "track-aircraft", "prefetch-radius-km", "prefetch-lookahead-seconds", "prefetch-textures",
            "lazy-terrain-listing", "provisional-lod", "provisional-wait",
        ];
        foreach (QualityPreset preset in QualityPreset.All)
            Assert.Equal(hidden.Order(), preset.Settings.Keys.Order());
    }

    [Fact]
    public void A_new_settings_file_lists_every_preset_value_in_its_layout()
    {
        SettingsDocument settings = Settings(QualityPreset.Ultra);
        Assert.DoesNotContain(settings.Lines, l => l.StartsWith("# --- Set in the OrthoStreamXP window", StringComparison.Ordinal));
        Assert.Equal(("1024", "6"), (settings.Get("build-memory"), settings.Get("max-wait")));
    }

    [Fact]
    public void Applying_a_review_changes_the_settings_and_remembers_the_presets_own_ones()
    {
        SettingsDocument settings = Settings(QualityPreset.Sharp);
        TunedStore store = Store();
        FlightReview review = ReviewOf(-1,
            new Suggestion("max-wait", "", "6", "7.5", ""),
            new Suggestion("paging-k", "", "auto", "9800", ""),
            new Suggestion(null, "A finding", null, null, ""));

        Assert.True(TunedSettings.Apply(review, settings, store));

        Assert.Equal(("7.5", "9800"), (settings.Get("max-wait"), settings.Get("paging-k")));
        Assert.Equal(["max-wait"], store.For(-1).Keys); // paging-k is the screen's, shared by every preset
    }

    [Fact]
    public void A_review_for_another_preset_is_not_applied()
    {
        SettingsDocument settings = Settings(QualityPreset.Ultra);
        Assert.False(TunedSettings.Apply(ReviewOf(-1, new Suggestion("max-wait", "", "6", "7.5", "")), settings, Store()));
        Assert.Equal("6", settings.Get("max-wait"));
    }

    [Fact]
    public void Switching_presets_and_back_keeps_what_was_tuned_for_each()
    {
        SettingsDocument settings = Settings(QualityPreset.Extra);
        TunedStore store = Store();
        TunedSettings.Apply(ReviewOf(1, new Suggestion("max-wait", "", "6", "7.5", "")), settings, store);
        store.Save();

        TunedSettings.ChoosePreset(settings, QualityPreset.Sharp, Store());
        Assert.Equal("6", settings.Get("max-wait"));

        TunedSettings.ChoosePreset(settings, QualityPreset.Extra, Store()); // read back from the file
        Assert.Equal("7.5", settings.Get("max-wait"));
    }

    [Fact]
    public void Reset_puts_every_hidden_setting_back_and_forgets_the_tuning()
    {
        SettingsDocument settings = Settings(QualityPreset.Sharp);
        TunedStore store = Store();
        TunedSettings.Apply(ReviewOf(-1, new Suggestion("max-wait", "", "6", "7.5", ""), new Suggestion("paging-k", "", "auto", "9800", "")), settings, store);
        settings.Set("dds-build-workers", 3); // edited by hand in the file
        Assert.Equal(["max-wait", "paging-k"], TunedSettings.Changed(settings, QualityPreset.Sharp).Select(c => c.Setting.Key));
        Assert.True(TunedSettings.DiffersFromDefaults(settings, QualityPreset.Sharp));

        TunedSettings.Reset(settings, QualityPreset.Sharp, store);

        Assert.Empty(TunedSettings.Changed(settings, QualityPreset.Sharp));
        Assert.False(TunedSettings.DiffersFromDefaults(settings, QualityPreset.Sharp));
        Assert.Equal(("6", "auto", "8"), (settings.Get("max-wait"), settings.Get("paging-k"), settings.Get("dds-build-workers")));
        Assert.Equal(160, settings.GetInt("download-workers")); // shown in the window: the person's own choice
        Assert.Empty(store.For(-1));
    }

    [Fact]
    public void A_review_waits_on_disk_for_the_window_and_one_without_suggestions_removes_it()
    {
        string path = FlightReview.PathFor(_tree.PathOf("cache"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FlightReview review = ReviewOf(0, new Suggestion("max-wait", "Wait for other scenery before a stand-in", "4", "5.5", "Because."));

        review.Save(path);
        FlightReview loaded = FlightReview.Load(path)!;

        Assert.Equal((0, When, 30.0), (loaded.Level, loaded.When, loaded.MinutesInFlight));
        Assert.Equal(review.Suggestions, loaded.Suggestions);
        Assert.Contains("max-wait: 4 -> 5.5", review.Describe(), StringComparison.Ordinal);

        new FlightReview(0, When, 1, [], TooShort: true).Save(path); // a quick start and stop: the suggestions wait on
        Assert.True(File.Exists(path));

        ReviewOf(0).Save(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Values_read_in_plain_words()
    {
        Assert.Equal("automatic", TunedSettings.PagingK.Show("auto"));
        Assert.Equal("3.5 s", TunedSettings.MaxWait.Show("3.5"));
        Assert.Equal("1024 MB", TunedSettings.BuildMemory.Show("1024"));
    }
}

/// <summary>The photo textures' graphics memory: measured per texture size from what X-Plane wanted.</summary>
public sealed class GraphicsMemoryTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_texture_takes_the_detail_X_Plane_wanted_but_never_more_than_its_size()
    {
        // Next to the aircraft X-Plane wanted 4096 px; farther out 512 and 256.
        Dictionary<int, long> held = GraphicsMemory.HeldBytes([4096, 512, 256]);

        long far = GraphicsMemory.TextureBytes(512) + GraphicsMemory.TextureBytes(256);
        Assert.Equal(GraphicsMemory.TextureBytes(4096) + far, held[4096]);
        Assert.Equal(GraphicsMemory.TextureBytes(1024) + far, held[1024]); // a 1024 px texture holds 1024 at most
        Assert.Equal(11_184_810, GraphicsMemory.TextureBytes(4096));          // 8 MB of BC1, a third more for its levels
    }

    [Fact]
    public void A_flight_measures_the_peak_and_sizes_above_it_follow_the_test_flight()
    {
        var meter = new GraphicsMemoryMeter(flownSize: 2048);
        meter.Sample([2048, 2048, 512]);
        meter.Sample([2048]); // later, less: the peak stays
        GraphicsMemory memory = meter.Result();

        Assert.Equal(2 * GraphicsMemory.TextureBytes(2048) + GraphicsMemory.TextureBytes(512), memory.BytesAt(2048));
        // 4096 px was not flown: the measured 2048 px figure, in the test flight's proportion.
        double ratio = (double)GraphicsMemory.TestFlight.BytesAt(4096) / GraphicsMemory.TestFlight.BytesAt(2048);
        Assert.Equal((long)(memory.BytesAt(2048) * ratio), memory.BytesAt(4096));

        memory.Save(_tree.Root);
        Assert.Equal(memory.BytesAt(2048), GraphicsMemory.Load(_tree.Root).BytesAt(2048));
        Assert.Same(GraphicsMemory.TestFlight, GraphicsMemory.Load(_tree.PathOf("nothing-here")));
    }
}

/// <summary>The recommended preset: the sharpest the graphics card, the connection and the last flight allow.</summary>
public sealed class PresetAdvisorTests
{
    private const long Gb = 1L << 30;

    /// <summary>A flight at a preset that downloaded at <paramref name="mbits"/> for 5 minutes, with that share of real pictures.</summary>
    private static FlightReport Flight(string quality, double mbits, long reads = 10_000, long standIns = 0) =>
        new(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow, quality,
        [
            new(0, 51.1, 16.9, 0, 0, 0, 0, 0, 0),
            new(300, 51.2, 16.9, 120, 1000, (long)(mbits * 1e6 / 8 * 300), 10, standIns, reads),
        ]);

    [Fact]
    public void Without_a_flight_it_is_Sharp_or_Balanced_on_a_small_graphics_card()
    {
        Recommendation start = PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, []);
        Assert.Equal(QualityPreset.Sharp, start.Preset);
        Assert.Contains("Test your connection or fly once", start.Reason, StringComparison.Ordinal);
        Assert.Equal(QualityPreset.Balanced, PresetAdvisor.Recommend(5 * Gb, GraphicsMemory.TestFlight, []).Preset);
        Assert.Equal(QualityPreset.Sharp, PresetAdvisor.Recommend(null, GraphicsMemory.TestFlight, []).Preset);
    }

    [Theory]
    [InlineData(150, 16, "Extra")]  // fast and a big card: the finest
    [InlineData(60, 16, "Ultra")]   // not fast enough for Extra's 4 times the downloads
    [InlineData(150, 6, "Sharp")]   // 4096 px textures and X-Plane would not fit in 6 GB
    [InlineData(10, 16, "Balanced")]
    public void After_a_flight_it_is_the_sharpest_the_connection_and_the_card_allow(double mbits, int cardGb, string expected)
    {
        Recommendation r = PresetAdvisor.Recommend(cardGb * Gb, GraphicsMemory.TestFlight, [Flight("level0_4096-4096", mbits)]);
        Assert.Equal(expected, r.Preset.Name);
        Assert.Contains("Mbit/s", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flight_over_cached_scenery_says_nothing_about_the_connection_but_an_earlier_fast_one_does()
    {
        FlightReport cached = Flight("level+1_4096-4096", 2); // 75 MB in 5 minutes: from the cache
        Assert.Equal(QualityPreset.Sharp, PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, [cached]).Preset);
        Assert.Equal(QualityPreset.Extra, PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, [cached, Flight("level+1_4096-4096", 140)]).Preset);
    }

    [Fact]
    public void A_preset_that_was_often_blurry_is_not_recommended_again()
    {
        FlightReport blurry = Flight("level+1_4096-4096", 150, reads: 10_000, standIns: 900); // 91 % real at Extra
        Assert.Equal(QualityPreset.Ultra, PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, [blurry]).Preset);
    }
}

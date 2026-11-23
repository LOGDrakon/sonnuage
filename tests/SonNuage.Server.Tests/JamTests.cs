using System.Text;
using SonNuage.Contracts;
using SonNuage.Server.Jam;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Tests;

/// <summary>Logique pure de « Rejoindre un Jam » : lecture d'un lien de partage et de l'état de lecture de l'hôte.</summary>
public sealed class JamTests
{
    [Theory]
    [InlineData("https://open.spotify.com/socialsession/3t2Yn4EKMk4APNPw22vlZg?si=abc", "3t2Yn4EKMk4APNPw22vlZg")]
    [InlineData("open.spotify.com/socialsession/AbC123dEf456GhI789jk", "AbC123dEf456GhI789jk")]
    [InlineData("3t2Yn4EKMk4APNPw22vlZg", "3t2Yn4EKMk4APNPw22vlZg")]
    [InlineData("spotify:socialsession:3t2Yn4EKMk4APNPw22vlZg", "3t2Yn4EKMk4APNPw22vlZg")]
    public void A_direct_link_or_token_yields_the_session_id(string link, string expected) =>
        Assert.Equal(expected, SpotifyLink.FromText(link));

    [Theory]
    [InlineData("https://spotify.link/PEogbB1787b")]
    [InlineData("hello")]
    [InlineData("")]
    public void A_short_link_or_junk_is_not_read_without_following_it(string link) =>
        Assert.Null(SpotifyLink.FromText(link));

    [Fact]
    public void Join_request_detects_the_service_from_the_link()
    {
        Assert.Equal(SonNuage.Contracts.Jam.Spotify, SonNuage.Contracts.JamJoinRequest.Service("https://spotify.link/x"));
        Assert.Equal(SonNuage.Contracts.Jam.Spotify, SonNuage.Contracts.JamJoinRequest.Service("https://open.spotify.com/socialsession/x"));
        Assert.Equal(SonNuage.Contracts.Jam.Deezer, SonNuage.Contracts.JamJoinRequest.Service("https://deezer.com/jam/x"));
        Assert.Null(SonNuage.Contracts.JamJoinRequest.Service("https://example.com/x"));
    }

    [Fact]
    public void The_cluster_response_gives_the_host_track_and_position()
    {
        var json = """
        {"active_device_id":"d1","server_timestamp_ms":"1794940457100","player_state":{
          "track":{"uri":"spotify:track:0bq82vecdWk6Ih5u76EZwO","metadata":{"title":"Pizza in the Cosmos"}},
          "position_as_of_timestamp":"2565","timestamp":"1794939287197","is_paused":true,"duration":"218058"}}
        """;
        var state = ClusterState.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Equal("0bq82vecdWk6Ih5u76EZwO", state!.TrackId);
        Assert.Equal("Pizza in the Cosmos", state.Title);
        Assert.True(state.IsPaused);
        Assert.Equal(218058, state.DurationMs);
    }

    [Fact]
    public void A_dealer_cluster_update_is_decoded_from_protobuf()
    {
        // Les push dealer sont du protobuf ClusterUpdate, pas du JSON (comme go-librespot).
        // ProvidedTrack{ uri=1 } ; PlayerState{ timestamp=1, track=7, position=10, duration=11, is_paused=13, next_tracks=20 }
        // Cluster{ player_state=3 } ; ClusterUpdate{ cluster=1 }
        var track = Pb.Msg(Pb.Str(1, "spotify:track:abc"));
        var next = Pb.Msg(Pb.Str(1, "spotify:track:def"));
        var playerState = Pb.Concat(Pb.Varint(1, 2000), Pb.Len(7, track), Pb.Varint(10, 1000), Pb.Varint(11, 200000), Pb.Varint(13, 0), Pb.Len(20, next));
        var cluster = Pb.Len(3, playerState);
        var clusterUpdate = Pb.Len(1, cluster);

        var state = ClusterState.ParseClusterUpdate(clusterUpdate);
        Assert.NotNull(state);
        Assert.Equal("abc", state!.TrackId);
        Assert.Equal(1000, state.PositionMs);
        Assert.Equal(2000, state.TimestampUnixMs);
        Assert.False(state.IsPaused);
        Assert.Equal(["spotify:track:def"], state.NextTrackUris);
    }

    [Theory]
    [InlineData("\"is_playing\":false,\"is_paused\":false", true)]
    [InlineData("\"is_playing\":true,\"is_paused\":true", true)]
    [InlineData("\"is_playing\":true,\"is_paused\":false", false)]
    [InlineData("\"is_paused\":false", false)]
    public void A_json_state_plays_only_if_playing_and_not_paused(string flags, bool paused)
    {
        // Hôte arrêté (fin de file) : is_playing faux sans is_paused. Absent, is_playing ne dit rien.
        var json = """{"player_state":{"track":{"uri":"spotify:track:a"},"position_as_of_timestamp":"0",""" + flags + "}}";
        Assert.Equal(paused, ClusterState.Parse(Encoding.UTF8.GetBytes(json))!.IsPaused);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 0, false)]
    public void A_protobuf_state_plays_only_if_playing_and_not_paused(int isPlaying, int isPaused, bool paused)
    {
        // PlayerState{ is_playing=12, is_paused=13 } ; -1 : is_playing absent.
        var track = Pb.Msg(Pb.Str(1, "spotify:track:abc"));
        byte[][] fields = isPlaying < 0
            ? [Pb.Len(7, track), Pb.Varint(13, isPaused)]
            : [Pb.Len(7, track), Pb.Varint(12, isPlaying), Pb.Varint(13, isPaused)];
        var update = Pb.Len(1, Pb.Len(3, Pb.Concat(fields)));
        Assert.Equal(paused, ClusterState.ParseClusterUpdate(update)!.IsPaused);
    }

    [Fact]
    public void A_protobuf_length_past_the_end_is_not_read()
    {
        // Longueur annoncée énorme (débordement de i + len) : message ignoré, sans exception ni lecture hors borne.
        byte[] huge = [0x0a, 0xff, 0xff, 0xff, 0xff, 0x07, 0x01];
        Assert.Null(ClusterState.ParseClusterUpdate(huge));
    }

    /// <summary>Encodeur protobuf minimal pour fabriquer des messages de test.</summary>
    private static class Pb
    {
        public static byte[] Varint(int field, long value)
        {
            var tag = EncodeVarint(((long)field << 3) | 0);
            return [.. tag, .. EncodeVarint(value)];
        }
        public static byte[] Str(int field, string s) => Len(field, Encoding.UTF8.GetBytes(s));
        public static byte[] Len(int field, byte[] bytes)
        {
            var tag = EncodeVarint(((long)field << 3) | 2);
            return [.. tag, .. EncodeVarint(bytes.Length), .. bytes];
        }
        public static byte[] Msg(params byte[] field) => field;
        public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        private static byte[] EncodeVarint(long value)
        {
            var bytes = new List<byte>();
            var v = (ulong)value;
            do { var b = (byte)(v & 0x7f); v >>= 7; if (v != 0) b |= 0x80; bytes.Add(b); } while (v != 0);
            return [.. bytes];
        }
    }

    [Fact]
    public void A_dealer_json_cluster_push_gives_the_host_track_and_queue()
    {
        // Appareil enregistré en JSON → le dealer pousse du JSON {"cluster":{"player_state":…}}.
        var json = """
        {"cluster":{"active_device_id":"social-connect-x","player_state":{"timestamp":"1794953916659",
          "track":{"uri":"spotify:track:2PYXOG6GdxF9sUIH2NfOAD"},"position_as_of_timestamp":"0","duration":"244320",
          "is_paused":false,"next_tracks":[{"uri":"spotify:track:6NgYIETQ8U72CVfkzYhK30"},{"uri":"spotify:delimiter"}]}},
         "update_reason":"DEVICE_STATE_CHANGED"}
        """;
        var state = ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Equal("2PYXOG6GdxF9sUIH2NfOAD", state!.TrackId);
        Assert.Equal(244320, state.DurationMs);
        Assert.False(state.IsPaused);
        Assert.Equal(["spotify:track:6NgYIETQ8U72CVfkzYhK30"], state.NextTrackUris);
    }

    [Fact]
    public void The_host_position_compensates_the_spotify_clock_offset()
    {
        // Notre horloge a 2 s d'avance sur celle de Spotify : sans compensation on croirait l'hôte 2 s plus loin.
        var at = DateTimeOffset.UtcNow;
        var state = new ClusterState("spotify:track:x", "X", 10_000, at.ToUnixTimeMilliseconds() - 2_000, IsPaused: false, 200_000);
        Assert.Equal(14, state.PositionSecondsAt(at.AddSeconds(2), spotifyClockOffsetMs: 0), precision: 1);
        Assert.Equal(12, state.PositionSecondsAt(at.AddSeconds(2), spotifyClockOffsetMs: 2_000), precision: 1);
    }

    [Theory]
    [InlineData("""{"session":{"session_id":"s"},"reason":"SESSION_DELETED"}""", true)]
    [InlineData("""{"reason":"YOU_WERE_KICKED"}""", true)]
    [InlineData("""{"update":{"reason":"YOU_LEFT"}}""", true)]
    [InlineData("""{"reason":"YOU_WERE_REMOVED_UNSUPPORTED_CONTENT"}""", true)]
    [InlineData("""{"reason":"USER_JOINED"}""", false)]
    [InlineData("""{"reason":"SESSION_MEMBER_PAUSE_CHANGED"}""", false)]
    [InlineData("""not json""", false)]
    public void A_session_update_tells_whether_the_jam_is_over(string payload, bool over) =>
        Assert.Equal(over, SessionUpdate.EndsJam(Encoding.UTF8.GetBytes(payload)));

    [Theory]
    [InlineData("http://127.0.0.1:2375/containers/json")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://example.com/socialsession")]
    [InlineData("http://spotify.link/abc")]
    public async Task A_link_outside_spotify_is_never_fetched(string link)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(handler), link, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_short_link_redirecting_outside_spotify_is_not_followed()
    {
        var handler = new RecordingHandler(req => req.RequestUri!.Host == "spotify.link"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("http://10.0.0.1/admin") } }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(handler, disposeHandler: true), "https://spotify.link/PEogbB1787b", CancellationToken.None));
        Assert.Equal(["spotify.link"], handler.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task A_short_link_resolves_through_spotify_redirects()
    {
        var handler = new RecordingHandler(req => req.RequestUri!.Host == "spotify.link"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://open.spotify.com/socialsession/3t2Yn4EKMk4APNPw22vlZg?si=x") } }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        Assert.Equal("3t2Yn4EKMk4APNPw22vlZg", await SpotifyLink.ResolveAsync(new HttpClient(handler), "spotify.link/PEogbB1787b", CancellationToken.None));
    }

    [Fact]
    public async Task A_short_link_that_cannot_be_reached_is_a_bad_link_not_an_error()
    {
        // DNS en échec ou délai dépassé : le lien est refusé (400), pas une erreur 500.
        var handler = new RecordingHandler(_ => throw new HttpRequestException("dns"));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(handler), "https://spotify.link/PEogbB1787b", CancellationToken.None));
        var slow = new RecordingHandler(_ => throw new TaskCanceledException("timeout"));
        Assert.Null(await SpotifyLink.ResolveAsync(new HttpClient(slow), "https://spotify.link/PEogbB1787b", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"access_token":""")]
    [InlineData("""{"access_token":7,"expires_in":3600}""")]
    [InlineData("""[1]""")]
    public async Task A_failed_token_refresh_is_null_not_an_exception(string? body)
    {
        // Réseau en panne ou réponse illisible : comme un refus, le suivi garde son jeton et réessaiera.
        var handler = new RecordingHandler(_ => body is null
            ? throw new HttpRequestException("down")
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        var client = new SpotifyClient(new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<SpotifyClient>.Instance);
        Assert.Null(await client.RefreshAsync("refresh", CancellationToken.None));
    }

    [Fact]
    public async Task A_token_refresh_reads_the_new_token()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"new","expires_in":1800,"refresh_token":"r2"}""") });
        var client = new SpotifyClient(new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<SpotifyClient>.Instance);
        var fresh = await client.RefreshAsync("refresh", CancellationToken.None);
        Assert.Equal(("new", "r2"), (fresh!.AccessToken, fresh.RefreshToken));
    }

    [Theory]
    [InlineData(1e300, 86_400_000)]
    [InlineData(-5, 0)]
    public void A_seek_position_is_kept_within_a_day(double seconds, long expectedMs) =>
        Assert.Equal(expectedMs, (long)SpotifyClient.ControlCommand(JamControl.Seek, seconds)!["position"]!);

    [Fact]
    public void A_seek_to_an_unreal_position_is_not_a_command() =>
        Assert.Null(SpotifyClient.ControlCommand(JamControl.Seek, double.NaN));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var res = respond(request);
            res.RequestMessage = request;
            return Task.FromResult(res);
        }
    }

    [Fact]
    public void An_import_takes_the_surest_recording_and_never_another_version()
    {
        RankedEntry Ranked(string id, int score, double duration, string? title = null) =>
            new(new YtEntry(id, title ?? "Artist - Song (Official Video)", "Artist", duration, null), score);
        var song = new JamTrackDto("Song", "Artist", null, 200, null);

        // Le meilleur verdict gagne ; à égalité, le classement de la recherche départage.
        Assert.Equal("exact", JamResolver.PickImport([Ranked("near", 90, 214), Ranked("exact", 60, 200)], song)?.Entry.Id);
        Assert.Equal("first", JamResolver.PickImport([Ranked("second", 50, 200), Ranked("first", 80, 200)], song)?.Entry.Id);
        // Un remix bien classé ne remplace pas l'original ; une durée trop éloignée non plus ; un autre titre non plus.
        Assert.Null(JamResolver.PickImport([Ranked("remix", 90, 200, "Artist - Song (Sare Remix)")], song));
        Assert.Null(JamResolver.PickImport([Ranked("long", 90, 260)], song));
        Assert.Null(JamResolver.PickImport([Ranked("other", 90, 200, "Artist - Another Tune")], song));
    }

    [Fact]
    public void An_import_prefers_the_artists_own_upload_to_a_fan_upload()
    {
        // Cas réel (« Cardiac Arrest », Alfie Castley) : une traduction de fan était importée à la place du titre.
        var song = new JamTrackDto("Cardiac Arrest", "Alfie Castley", null, 147, "NL8RL2319905");
        var query = new MatchQuery(song.Artist, song.Title, song.DurationSeconds);
        var ranked = YoutubeMatcher.Rank(query, [
            new YtEntry("y8PIRe9dowM", "Cardiac Arrest", null, null, null),
            new YtEntry("fanupload01", "[和訳] Cardiac Arrest - Alfie Castley", "Cro_", 147, null),
            new YtEntry("indolirik01", "Alfie Castley - Cardiac Arrest | Lirik Terjemahan", "Indolirik", 147, null),
            new YtEntry("y8PIRe9dowM", "Cardiac Arrest", "Alfie Castley", 147, null),
        ]);
        Assert.Equal("y8PIRe9dowM", JamResolver.PickImport(ranked, song)?.Entry.Id);
    }

    [Theory]
    [InlineData("Cardiac Arrest", "Alfie Castley", "[和訳] Cardiac Arrest - Alfie Castley", "Cro_")]
    [InlineData("Stereo Hearts (feat. Adam Levine)", "Gym Class Heroes, Adam Levine", "Gym Class Heroes - Stereo Hearts (Lyrics)", "Kids Dance Party")]
    [InlineData("Bohemian Rhapsody - Remastered 2011", "Queen", "Queen – Bohemian Rhapsody (Official Video Remastered)", "Queen Official")]
    public void An_import_is_named_after_the_hosts_track_not_the_video(string title, string artist, string videoTitle, string channel)
    {
        // Le titre d'une vidéo est souvent à l'envers (« Titre - Artiste ») ou habillé : le Jam connaît le vrai.
        var song = new JamTrackDto(title, artist, null, 200, null);
        var video = JamResolver.ImportVideo(new YtEntry("abcdefghijk", videoTitle, channel, 200, null), song);

        var parsed = TitleParser.Parse(video.Title, video.Channel);
        Assert.Equal(artist, parsed.Artist);
        Assert.StartsWith(title.Split(" - ")[0].Split(" (")[0], parsed.Title);
        Assert.Equal("abcdefghijk", video.VideoId);
        Assert.Equal(channel, video.Channel);
    }

    [Fact]
    public void The_host_view_shows_the_hosts_track_queue_and_position()
    {
        var next = new TrackDto(Guid.NewGuid(), "Next", Guid.Empty, "B", Guid.Empty, "", null, null, 180, "flac", true, null, false, false);
        var state = JamStateDto.Inactive with
        {
            Active = true,
            Audio = JamAudio.RemoteOnly,
            SourceTrackId = "4uLU6hMCjMI75M1A2tKUQC",
            Track = new JamTrackDto("Over This", "Artist", null, 200, null),
            Resolve = JamResolve.Unavailable,
            IsPlaying = true,
            Upcoming = [next],
        };

        var view = SonNuage.UI.Services.JamSession.HostView(state, 61.5);

        Assert.Equal("Over This", view.Current?.Title);
        Assert.Equal("Artist", view.Current?.ArtistName);
        Assert.Equal([view.Current!, next], view.Queue);
        Assert.True(view.IsPlaying);
        Assert.Equal(61.5, view.PositionSeconds);
        Assert.Equal(200, view.DurationSeconds);
        // Le même titre de l'hôte garde le même identifiant d'un état à l'autre (le lecteur ne le voit pas « changer »).
        Assert.Equal(view.Current!.Id, SonNuage.UI.Services.JamSession.HostView(state, 70).Current!.Id);
        Assert.Null(SonNuage.UI.Services.JamSession.HostView(state with { Track = null, SourceTrackId = null }, 0).Current);
    }

    [Fact]
    public void An_ad_or_episode_from_the_host_is_not_a_track_to_follow()
    {
        var json = """{"player_state":{"track":{"uri":"spotify:ad:123"},"position_as_of_timestamp":"5000","timestamp":"1","duration":"30000"}}""";
        var state = ClusterState.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Null(state!.TrackId);
    }

    [Theory]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":42},"position_as_of_timestamp":"0"}}}""")]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":"spotify:track:a","metadata":{"title":7}}}}}""")]
    [InlineData("""{"cluster":{"player_state":{"track":{"uri":"spotify:track:a"},"next_tracks":[1,{"uri":2}]}}}""")]
    public void A_push_with_unexpected_value_types_is_ignored_not_thrown(string json)
    {
        var state = ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json));
        Assert.True(state is null || state.TrackUri == "spotify:track:a");
    }

    [Fact]
    public void A_json_push_carries_the_spotify_server_time()
    {
        var json = """{"cluster":{"server_timestamp_ms":"1794953917038","player_state":{"timestamp":"1794953916659","track":{"uri":"spotify:track:a"},"position_as_of_timestamp":"0"}}}""";
        Assert.Equal(1794953917038, ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json))!.ServerTimestampMs);
    }

    [Fact]
    public void A_state_without_a_track_means_nothing_plays()
    {
        // L'hôte n'a plus rien (file finie) : les invités doivent le voir arrêté, pas garder le dernier titre « en lecture ».
        var json = """{"player_state":{"position_as_of_timestamp":"0","is_paused":false}}""";
        var state = ClusterState.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(state);
        Assert.Null(state.TrackId);
        Assert.True(state.IsPaused);
    }

    [Fact]
    public void A_paused_position_does_not_drift_but_a_playing_one_advances()
    {
        var at = DateTimeOffset.UtcNow;
        var paused = new ClusterState("spotify:track:x", "X", 30_000, at.ToUnixTimeMilliseconds(), IsPaused: true, 200_000);
        Assert.Equal(30, paused.PositionSecondsAt(at.AddSeconds(10)), precision: 0);

        var playing = paused with { IsPaused = false };
        Assert.Equal(40, playing.PositionSecondsAt(at.AddSeconds(10)), precision: 0);
    }

    [Theory]
    [InlineData("4uLU6hMCjMI75M1A2tKUQC", "93bc414a606747b2b612491ef83d5a3e")]
    [InlineData("0000000000000000000000", "00000000000000000000000000000000")]
    [InlineData("not-base62!", null)]
    [InlineData("", null)]
    public void A_track_id_becomes_the_metadata_gid(string id, string? gid) =>
        Assert.Equal(gid, SpotifyClient.TrackGid(id));

    [Fact]
    public void Track_metadata_gives_the_isrc_and_every_artist()
    {
        var json = """
            {"gid":"k7xBSmBnR7K2EkkfJD1aPg==","name":"Stereo Hearts (feat. Adam Levine)",
             "album":{"name":"The Papercut Chronicles II"},
             "artist":[{"name":"Gym Class Heroes"},{"name":"Adam Levine"}],
             "duration":210960,
             "external_id":[{"type":"isrc","id":"USAT21101321"}]}
            """;
        var track = SpotifyClient.ParseTrackMetadata(Encoding.UTF8.GetBytes(json));
        Assert.Equal(new JamTrackDto("Stereo Hearts (feat. Adam Levine)", "Gym Class Heroes, Adam Levine", "The Papercut Chronicles II", 210.96, "USAT21101321"), track);
        Assert.Null(SpotifyClient.ParseTrackMetadata(Encoding.UTF8.GetBytes("""{"name":""}""")));
        Assert.Null(SpotifyClient.ParseTrackMetadata(Encoding.UTF8.GetBytes("<html>")));
    }

    [Fact]
    public void The_host_device_is_read_from_the_cluster()
    {
        // Les commandes (pause, saut, file) visent l'appareil qui joue chez l'hôte.
        var json = """{"cluster":{"active_device_id":"abc123","player_state":{"track":{"uri":"spotify:track:x"}}}}""";
        Assert.Equal("abc123", ClusterState.ParseClusterUpdate(Encoding.UTF8.GetBytes(json))?.ActiveDeviceId);
    }

    [Theory]
    [InlineData(JamControl.Pause, null, """{"endpoint":"pause"}""")]
    [InlineData(JamControl.Resume, null, """{"endpoint":"resume"}""")]
    [InlineData(JamControl.Next, null, """{"endpoint":"skip_next"}""")]
    [InlineData(JamControl.Previous, null, """{"endpoint":"skip_prev"}""")]
    [InlineData(JamControl.Seek, 61.5, """{"endpoint":"seek_to","value":61500,"position":61500,"relative":"beginning"}""")]
    public void Controls_become_connect_state_commands(JamControl action, double? seconds, string expected)
    {
        var command = SpotifyClient.ControlCommand(action, seconds)!;
        command.Remove("logging_params");
        Assert.Equal(expected, command.ToJsonString());
    }

    [Fact]
    public void A_seek_without_a_position_is_not_a_command() =>
        Assert.Null(SpotifyClient.ControlCommand(JamControl.Seek, null));

    [Fact]
    public void A_queued_track_is_marked_as_queued()
    {
        var command = SpotifyClient.QueueCommand("spotify:track:abc");
        command.Remove("logging_params");
        Assert.Equal("""{"endpoint":"add_to_queue","track":{"uri":"spotify:track:abc","metadata":{"is_queued":"true"},"provider":"queue"}}""", command.ToJsonString());
    }

    [Fact]
    public void Web_api_search_results_are_read()
    {
        var json = """
            {"tracks":{"items":[
              {"uri":"spotify:track:a","name":"Hide and Seek","duration_ms":272000,"artists":[{"name":"Imogen Heap"}],"external_ids":{"isrc":"GBAHS0500183"}},
              {"uri":"spotify:episode:x","name":"Podcast"},
              {"name":"no uri"}]}}
            """;
        var hits = SpotifyClient.ParseSearch(Encoding.UTF8.GetBytes(json));
        var hit = Assert.Single(hits);
        Assert.Equal("spotify:track:a", hit.Uri);
        Assert.Equal(new JamTrackDto("Hide and Seek", "Imogen Heap", null, 272, "GBAHS0500183"), hit.Track);
    }

    [Fact]
    public void Pathfinder_search_results_are_read()
    {
        var json = """
            {"data":{"searchV2":{"tracksV2":{"items":[
              {"item":{"data":{"__typename":"Track","uri":"spotify:track:c","name":"Emperors","duration":{"totalMilliseconds":128000},
                "artists":{"items":[{"profile":{"name":"nihmune"}}]}}}},
              {"item":{"data":{"__typename":"RestrictedContent"}}}]}}}}
            """;
        var hit = Assert.Single(SpotifyClient.ParseSearch(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("spotify:track:c", hit.Uri);
        Assert.Equal(new JamTrackDto("Emperors", "nihmune", null, 128, null), hit.Track);
    }

    [Fact]
    public void The_search_query_hash_is_found_in_the_web_player_code()
    {
        const string chunk = """let s0=[],s1=new _.l("searchTracks","query","b02683192a98dde7966b5e6655a79eeb62713eab703eda9902c932818dd52751",null);""";
        Assert.Equal("b02683192a98dde7966b5e6655a79eeb62713eab703eda9902c932818dd52751", SpotifyClient.FindQueryHash(chunk, "searchTracks"));
        Assert.Null(SpotifyClient.FindQueryHash("nothing", "searchTracks"));
        // Le même numéro de morceau a un hash pour le JS et un pour le CSS : on les essaie tous.
        const string main = """...4406:"xpui-routes-search",4563:"x"...{4406:"92c90ba3",4563:"aaaa1111"}...{4406:"a0a5ee13"}""";
        Assert.Equal(["92c90ba3", "a0a5ee13"], SpotifyClient.FindChunkHashes(main, "xpui-routes-search"));
    }

    [Fact]
    public void Playing_now_starts_that_track_instead_of_skipping_into_the_queue()
    {
        // « Ajouter puis suivant » jouerait le plus ancien titre de la file, pas celui demandé.
        var command = SpotifyClient.PlayCommand("spotify:track:abc");
        command.Remove("logging_params");
        Assert.Equal("""{"endpoint":"play","context":{"uri":"spotify:track:abc","url":"context://spotify:track:abc","metadata":{}},"options":{"skip_to":{"track_uri":"spotify:track:abc"}}}""", command.ToJsonString());
    }

    [Theory]
    [InlineData("Here Comes The Sun - Remastered 2009", "herecomesthesun ")]
    [InlineData("Stereo Hearts (feat. Adam Levine)", "stereohearts ")]
    [InlineData("Song feat. Someone", "song ")]
    [InlineData("Hide and Seek", "hideandseek ")]
    public void The_library_is_searched_by_the_bare_title_too(string title, string prefix)
    {
        // « Titre - Remastered 2009 » sur Spotify, « Titre » dans la bibliothèque : la clé du titre entier ne le trouvait pas.
        Assert.Contains(prefix, JamResolver.LibraryPrefixes(title));
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Lecture des réponses InnerTube pour la lecture YouTube directe (Jam) : choix du titre et du flux audio.</summary>
public sealed class YoutubeDirectTests
{
    [Theory]
    [InlineData("3:04", 184)]
    [InlineData("1:02:03", 3723)]
    [InlineData(" 0:59 ", 59)]
    [InlineData("Song", 0)]
    [InlineData("", 0)]
    [InlineData("12", 0)]
    public void Durations_are_read_from_result_text(string text, double expected) =>
        Assert.Equal(expected, YoutubeDirect.ParseDuration(text));

    private static JsonNode Songs(params (string Id, string Duration)[] songs) =>
        Titled(songs.Select(s => (s.Id, "Title", s.Duration)).ToArray());

    private static JamTrackDto Host(string title, double seconds) => new(title, "Artist", null, seconds, null);

    private static JsonNode Titled(params (string Id, string Title, string Duration)[] songs) => Titled("Artist", songs);

    private static JsonNode Titled(string artist, params (string Id, string Title, string Duration)[] songs)
    {
        var items = new JsonArray();
        foreach (var (id, title, duration) in songs)
        {
            JsonObject Column(params string[] texts) => new()
            {
                ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["runs"] = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["text"] = t }).ToArray()) },
                },
            };
            items.Add(new JsonObject
            {
                ["musicResponsiveListItemRenderer"] = new JsonObject
                {
                    ["playlistItemData"] = new JsonObject { ["videoId"] = id },
                    ["flexColumns"] = new JsonArray(Column(title), Column(artist, " • ", "Album", " • ", duration)),
                },
            });
        }
        var shelf = new JsonObject { ["musicShelfRenderer"] = new JsonObject { ["contents"] = items } };
        return new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["tabbedSearchResultsRenderer"] = new JsonObject
                {
                    ["tabs"] = new JsonArray(new JsonObject
                    {
                        ["tabRenderer"] = new JsonObject
                        {
                            ["content"] = new JsonObject { ["sectionListRenderer"] = new JsonObject { ["contents"] = new JsonArray(shelf) } },
                        },
                    }),
                },
            },
        };
    }

    [Fact]
    public void The_song_whose_duration_matches_spotify_is_preferred()
    {
        // Le premier résultat est une version live (4:30) ; la version studio (3:04) colle à Spotify (183 s).
        var root = Songs(("live", "4:30"), ("studio", "3:04"));
        Assert.Equal("studio", YoutubeDirect.PickSong(root, Host("Title", 183)));
    }

    [Fact]
    public void Without_a_close_duration_nothing_is_picked()
    {
        // Mieux vaut l'import du serveur qu'un autre morceau : aucune durée proche, aucun choix.
        Assert.Null(YoutubeDirect.PickSong(Songs(("a", "9:00"), ("b", "8:00")), Host("Title", 183)));
        Assert.Equal("a", YoutubeDirect.PickSong(Songs(("a", "9:00")), Host("Title", 0)));
        Assert.Null(YoutubeDirect.PickSong(JsonNode.Parse("{}"), Host("Title", 183)));
    }

    [Fact]
    public void A_remix_is_not_picked_for_the_original()
    {
        // Cas réel : « Hide and Seek » chez l'hôte, le remix sort en premier sur YouTube Music.
        var root = Titled(("remix", "Hide and Seek (Sare Remix)", "2:23"), ("original", "Hide and Seek", "3:04"));
        Assert.Equal("original", YoutubeDirect.PickSong(root, Host("Hide and Seek", 183)));
        Assert.Null(YoutubeDirect.PickSong(Titled(("remix", "Hide and Seek (Sare Remix)", "3:04")), Host("Hide and Seek", 183)));
    }

    [Fact]
    public void Another_artist_is_not_picked()
    {
        var host = new JamTrackDto("Hide and Seek", "Imogen Heap", null, 183, null);
        Assert.Null(YoutubeDirect.PickSong(Titled(("other", "Hide and Seek", "3:04")), host));
    }

    [Fact]
    public void Opus_audio_with_a_direct_url_is_chosen()
    {
        var player = JsonNode.Parse("""
            {"playabilityStatus":{"status":"OK"},"streamingData":{"adaptiveFormats":[
              {"mimeType":"video/mp4","url":"v","bitrate":900000},
              {"mimeType":"audio/mp4; codecs=\"mp4a.40.2\"","url":"aac","bitrate":130000,"contentLength":"3000000","approxDurationMs":"183000"},
              {"mimeType":"audio/webm; codecs=\"opus\"","url":"opus","bitrate":120000,"contentLength":"2900000","approxDurationMs":"183000"},
              {"mimeType":"audio/webm; codecs=\"opus\"","bitrate":160000}]}}
            """);
        var audio = YoutubeDirect.PickAudio("vid", player);
        Assert.NotNull(audio);
        Assert.Equal("opus", audio!.Url);
        Assert.Equal(183, audio.DurationSeconds);
        Assert.Equal(2_900_000, audio.ContentLength);
    }

    [Fact]
    public void A_refused_video_gives_no_audio() =>
        Assert.Null(YoutubeDirect.PickAudio("vid", JsonNode.Parse("""{"playabilityStatus":{"status":"LOGIN_REQUIRED"}}""")));

    [Fact]
    public void A_youtube_track_is_recognised_and_never_collides_with_a_library_id()
    {
        var track = YoutubeTracks.Create("orOgilmiL_4", "NO BATIDÃO", "ZXKAI", 89.7);
        Assert.Equal("orOgilmiL_4", YoutubeTracks.VideoId(track));
        Assert.Equal(track.Id, YoutubeTracks.IdFor("orOgilmiL_4"));
        Assert.NotEqual(track.Id, YoutubeTracks.IdFor("other"));
        Assert.False(YoutubeTracks.IsYoutube(track with { Format = "opus" }));
    }

    [Fact]
    public void The_visitor_id_is_read_from_the_service_worker_data()
    {
        var text = ")]}'\n" + """[["a",null,[[["x",1,2,3,4,5,6,7,8,9,10,11,12,"CgtWaXNpdG9yMTIz"]]]]]""";
        Assert.Equal("CgtWaXNpdG9yMTIz", YoutubeDirect.ParseVisitor(text));
    }

    [Theory]
    [InlineData("<html><body>Before you continue to YouTube</body></html>")]
    [InlineData(")]}'\n[[\"a\",null,[[[\"x\",1]]]]]")]
    [InlineData(")]}'\n[[\"a\",null,[[[0,1,2,3,4,5,6,7,8,9,10,11,12,42]]]]]")]
    [InlineData(")]}'\n[broken")]
    public void An_unexpected_service_worker_reply_gives_no_visitor_id(string text) =>
        Assert.Null(YoutubeDirect.ParseVisitor(text));

    [Fact]
    public async Task A_failed_isrc_search_falls_back_to_artist_and_title()
    {
        var youtube = new FakeYoutube { FailQuery = "USRC17607839" };
        var direct = new YoutubeDirect(youtube);

        var audio = await direct.FindAsync(new JamTrackDto("Title", "Artist", null, 183, "USRC17607839"), TestContext.Current.CancellationToken);

        Assert.Equal("studio", audio?.VideoId);
        Assert.Equal(["USRC17607839", "Artist Title"], youtube.Queries);
    }

    [Fact]
    public async Task A_forgotten_stream_is_asked_again()
    {
        // Android le signale quand l'URL ne répond plus : la suivante doit être neuve, pas celle du cache.
        var youtube = new FakeYoutube();
        var direct = new YoutubeDirect(youtube);
        var ct = TestContext.Current.CancellationToken;

        await direct.AudioAsync("studio", ct);
        await direct.AudioAsync("studio", ct);
        Assert.Equal(1, youtube.Players);

        direct.Forget("studio");
        await direct.AudioAsync("studio", ct);
        Assert.Equal(2, youtube.Players);
    }

    [Theory]
    [InlineData("Sign in to confirm you’re not a bot", 2, 2)]
    [InlineData("Sign in to confirm your age", 1, 1)]
    public async Task Only_a_bot_check_renews_the_visitor(string reason, int players, int visitors)
    {
        // Un visiteur neuf répond au contrôle anti-robot ; il ne change rien à une restriction d'âge ou de pays.
        var youtube = new FakeYoutube { Refusal = reason };
        var direct = new YoutubeDirect(youtube);

        Assert.Null(await direct.AudioAsync("studio", TestContext.Current.CancellationToken));
        Assert.Equal(players, youtube.Players);
        Assert.Equal(visitors, youtube.Visitors);
    }

    [Theory]
    [InlineData("""{"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Sign in to confirm you’re not a bot"}}""", true)]
    [InlineData("""{"playabilityStatus":{"status":"LOGIN_REQUIRED","messages":["Sign in to confirm you're not a bot."]}}""", true)]
    [InlineData("""{"playabilityStatus":{"status":"LOGIN_REQUIRED","reason":"Sign in to confirm your age"}}""", false)]
    [InlineData("""{"playabilityStatus":{"status":"UNPLAYABLE","reason":"The uploader has not made this video available in your country"}}""", false)]
    [InlineData("""{"playabilityStatus":{"status":"OK"}}""", false)]
    public void The_visitor_is_renewed_only_for_a_bot_check(string json, bool renew) =>
        Assert.Equal(renew, YoutubeDirect.NeedsNewVisitor(JsonNode.Parse(json)));

    /// <summary>
    /// YouTube : un visiteur, une recherche (« studio » à 3:04, ou une erreur pour <see cref="FailQuery"/>) et un
    /// lecteur (flux opus, ou un refus de raison <see cref="Refusal"/>). <see cref="Song"/> remplace le résultat, et la
    /// recherche attend <see cref="SearchGate"/> s'il est donné.
    /// </summary>
    internal sealed class FakeYoutube : HttpMessageHandler
    {
        public (string Title, string Artist, string Duration)? Song { get; init; }
        public Task? SearchGate { get; init; }
        public string? FailQuery { get; init; }
        public string? Refusal { get; init; }
        public List<string> Queries { get; } = [];
        public int Players { get; private set; }
        public int Visitors { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sw.js_data", StringComparison.Ordinal))
            {
                Visitors++;
                return Json(")]}'\n" + $"[[\"a\",null,[[[\"x\",1,2,3,4,5,6,7,8,9,10,11,12,\"visitor{Visitors}\"]]]]]");
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                var query = body["query"]!.GetValue<string>();
                Queries.Add(query);
                if (query == FailQuery) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (SearchGate is { } gate) await gate.WaitAsync(ct);
                return Json((Song is { } song ? Titled(song.Artist, ("studio", song.Title, song.Duration)) : Songs(("studio", "3:04"))).ToJsonString());
            }
            Players++;
            return Json(Refusal is { } reason
                ? new JsonObject { ["playabilityStatus"] = new JsonObject { ["status"] = "LOGIN_REQUIRED", ["reason"] = reason } }.ToJsonString()
                : """{"playabilityStatus":{"status":"OK"},"streamingData":{"adaptiveFormats":[{"mimeType":"audio/webm; codecs=\"opus\"","url":"opus","bitrate":120000}]}}""");
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>
/// <see cref="JamSession"/> côté appli, contre un faux serveur : le suivi survit aux coupures du flux, et la synchro
/// pilote le lecteur local sans troubler ce que l'utilisateur voit ou commande.
/// </summary>
public sealed class JamSessionTests
{
    private static readonly Uri Server = new("https://music.example.org/");
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task A_silent_stream_is_reopened_instead_of_freezing_the_jam()
    {
        // 45 s sans rien sur le flux : ServerSentEvents lève TimeoutException. Le suivi doit rouvrir le flux.
        var server = new FakeJamServer(Playing(JamAudio.RemoteOnly));
        server.Streams.Enqueue(Stream.Failing(new TimeoutException("Flux d'événements muet.")));
        using var jam = await SessionAsync(server, new FakePlayer());
        var ended = false;
        jam.Ended += () => ended = true;

        await jam.JoinAsync("https://spotify.link/x", JamAudio.RemoteOnly);
        await Until(() => server.Opened >= 2);
        server.Push(JamStateDto.Inactive);
        await Until(() => ended);

        Assert.False(jam.State.Active);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_the_follow()
    {
        var server = new FakeJamServer(Playing(JamAudio.RemoteOnly));
        using var jam = await SessionAsync(server, new FakePlayer());
        await jam.JoinAsync("https://spotify.link/x", JamAudio.RemoteOnly);
        var fail = true;
        jam.StateChanged += () => { if (fail) { fail = false; throw new InvalidOperationException("abonné en panne"); } };
        var ended = false;
        jam.Ended += () => ended = true;

        server.Push(Playing(JamAudio.RemoteOnly));
        await Until(() => !fail && server.Opened >= 2);
        server.Push(JamStateDto.Inactive);
        await Until(() => ended);
    }

    [Fact]
    public async Task In_host_only_mode_the_position_ticks_show_the_host()
    {
        // La boucle de synchro fait avancer la position affichée : ce qu'elle déclenche doit voir l'hôte, pas la file
        // locale en pause (la synchro contourne le Jam pour ses propres commandes, pas pour l'affichage).
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new SoloLink());
        await player.RestoreAsync();
        await player.PlayAsync([Library("Local")], 0);
        var server = new FakeJamServer(Playing(JamAudio.RemoteOnly));
        using var jam = await SessionAsync(server, player);
        await jam.JoinAsync("https://spotify.link/x", JamAudio.RemoteOnly);
        var seen = new List<string?>();
        player.StateChanged += () => { lock (seen) seen.Add(player.State.Current?.Title); };

        await Until(() => { lock (seen) return seen.Count >= 3; });

        lock (seen) Assert.All(seen, title => Assert.Equal("Host song", title));
    }

    [Fact]
    public async Task A_command_that_cannot_reach_the_server_is_reported()
    {
        var server = new FakeJamServer(Playing(JamAudio.RemoteOnly)) { ControlFails = new HttpRequestException("réseau coupé") };
        using var jam = await SessionAsync(server, new FakePlayer());
        await jam.JoinAsync("https://spotify.link/x", JamAudio.RemoteOnly);
        var failed = new List<string>();
        jam.Failed += failed.Add;

        await ((IPlayerRoute)jam).NextAsync();
        server.ControlFails = new TaskCanceledException("délai dépassé");
        await ((IPlayerRoute)jam).SeekAsync(10);

        Assert.Equal(2, failed.Count);
        Assert.All(failed, m => Assert.False(string.IsNullOrWhiteSpace(m)));
    }

    [Fact]
    public async Task A_local_file_shorter_than_the_hosts_track_is_not_restarted_at_its_end()
    {
        // Le fichier de la bibliothèque dure 150 s, le titre de l'hôte 200 s. Arrivé au bout, le lecteur web revient au
        // début, en pause : la synchro ne doit ni le relancer ni y sauter (le morceau recommencerait sans fin).
        var track = Library("Short", 150);
        var local = new FakePlayer();
        var server = new FakeJamServer(Playing(JamAudio.Guest, track, position: 100));
        using var jam = await SessionAsync(server, local);
        await jam.JoinAsync("https://spotify.link/x", JamAudio.Guest);
        await Until(() => local.State.Current?.Id == track.Id && local.State.IsPlaying);

        server.Push(Playing(JamAudio.Guest, track, position: 160));
        await Task.Delay(500);
        await local.SeekAsync(0);
        await local.TogglePlayAsync();
        await Task.Delay(2000);

        Assert.False(local.State.IsPlaying);
        Assert.Equal(0, local.State.PositionSeconds);
    }

    [Fact]
    public async Task A_slow_youtube_lookup_does_not_hold_back_the_next_track()
    {
        // YouTube met longtemps à répondre pour le titre en cours ; l'hôte passe à un titre de la bibliothèque : on le
        // suit tout de suite, sans attendre la fin de la recherche devenue inutile.
        var next = Library("Next");
        var local = new FakePlayer();
        var server = new FakeJamServer(Playing(JamAudio.Guest));
        using var jam = await SessionAsync(server, local, new Offline(TimeSpan.FromSeconds(4)));
        await jam.JoinAsync("https://spotify.link/x", JamAudio.Guest);
        await Task.Delay(300);

        server.Push(Playing(JamAudio.Guest, next) with { SourceTrackId = "spotify-2" });

        await Until(() => local.State.Current?.Id == next.Id, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Sound_cut_by_the_system_stays_paused_until_the_user_plays()
    {
        // Casque débranché en mode invité : Android coupe le son local et le dit à la route. La synchro ne doit pas le
        // relancer ; la lecture suivante de l'utilisateur le relance ici, sans rien envoyer à l'hôte (qui joue déjà).
        var track = Library("Song");
        var local = new FakePlayer();
        var server = new FakeJamServer(Playing(JamAudio.Guest, track));
        using var jam = await SessionAsync(server, local);
        IPlayerRoute route = jam;
        await jam.JoinAsync("https://spotify.link/x", JamAudio.Guest);
        await Until(() => local.State.Current?.Id == track.Id && local.State.IsPlaying);

        route.HoldLocalAudio();
        await local.TogglePlayAsync();
        await Task.Delay(2000);
        Assert.False(local.State.IsPlaying);

        await route.TogglePlayAsync();
        await Until(() => local.State.IsPlaying);
        Assert.Empty(server.Controls);
    }

    [Fact]
    public async Task A_pause_while_the_sound_is_held_still_goes_to_the_host()
    {
        var track = Library("Song");
        var local = new FakePlayer();
        var server = new FakeJamServer(Playing(JamAudio.Guest, track));
        using var jam = await SessionAsync(server, local);
        IPlayerRoute route = jam;
        await jam.JoinAsync("https://spotify.link/x", JamAudio.Guest);
        await Until(() => local.State.IsPlaying);

        route.HoldLocalAudio();
        await local.TogglePlayAsync();
        await route.SetPlayingAsync(false);

        var sent = JsonSerializer.Deserialize<JamControlRequest>(Assert.Single(server.Controls), JsonDefaults.Options);
        Assert.Equal(JamControl.Pause, sent?.Action);
    }

    // ---- Faux serveur ----

    private static JamStateDto Playing(JamAudio audio, TrackDto? resolved = null, double position = 30) => new(
        true, SonNuage.Contracts.Jam.Spotify, audio, "Host", new JamTrackDto("Host song", "Host artist", null, 200, null), true, position,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), resolved is null ? JamResolve.Pending : JamResolve.Matched, resolved,
        SourceTrackId: "spotify-1");

    private static TrackDto Library(string title, double seconds = 200) => new(
        Guid.NewGuid(), title, Guid.NewGuid(), "Artiste", Guid.NewGuid(), "Album", 1, 1, seconds, "mp3", false, 320, false, false);

    private static async Task Until(Func<bool> condition, TimeSpan? patience = null)
    {
        var until = DateTime.UtcNow + (patience ?? Patience);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition jamais remplie");
            await Task.Delay(20);
        }
    }

    private static async Task<JamSession> SessionAsync(FakeJamServer server, IPlayer player, HttpMessageHandler? youtube = null)
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var user = new UserDto(Guid.NewGuid(), "alice", Roles.User, DateTimeOffset.UnixEpoch);
        var account = await accounts.SaveSignedInAsync(Server, user);
        store.Data[account.SessionKey] = JsonSerializer.Serialize(
            new TokenResponse("access", DateTimeOffset.UtcNow.AddHours(1), "refresh", "media", DateTimeOffset.UtcNow.AddDays(1), user),
            JsonDefaults.Options);
        await accounts.SwitchAsync(account.Id);
        var factory = new HandlerFactory(server);
        var endpoint = new FixedEndpoint(Server);
        var auth = new AuthSession(store, endpoint, accounts, factory);
        await auth.EnsureLoadedAsync();
        return new JamSession(new ApiClient(factory, endpoint, auth), player, auth, new YoutubeDirect(youtube ?? new Offline()));
    }

    /// <summary>YouTube injoignable : la recherche directe rend null, aussitôt ou après <paramref name="delay"/>.</summary>
    private sealed class Offline(TimeSpan delay = default) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            throw new HttpRequestException("hors ligne");
        }
    }

    /// <summary>Un seul appareil sur le compte : le relais ne renvoie rien.</summary>
    private sealed class SoloLink : IPlaybackLink
    {
        public string DeviceId => "tab";
        public Func<PlaybackMessage, Task>? OnMessage { get; set; }
        public Func<Task>? OnConnected { get; set; }
        public Task StartAsync() => Task.CompletedTask;
        public Task SendAsync(PlaybackMessage message) => Task.CompletedTask;
    }

    /// <summary>
    /// Le serveur du Jam : « join » rend l'état initial, chaque flux ouvert (prochain de <see cref="Streams"/>, sinon un
    /// flux ordinaire) reçoit les états poussés, « control » est enregistré ou échoue avec <see cref="ControlFails"/>.
    /// </summary>
    private sealed class FakeJamServer(JamStateDto initial) : HttpMessageHandler
    {
        private Stream? _current;
        public Queue<Stream> Streams { get; } = [];
        public int Opened;
        public List<string> Controls { get; } = [];
        public Exception? ControlFails { get; set; }

        public void Push(JamStateDto state) => _current?.Push(state);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (path == SonNuage.Contracts.Jam.StreamRoute)
            {
                Interlocked.Increment(ref Opened);
                var stream = Streams.TryDequeue(out var next) ? next : new Stream();
                _current = stream;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }
            if (path == SonNuage.Contracts.Jam.ControlRoute)
            {
                if (ControlFails is { } failure) throw failure;
                Controls.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/join", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(initial, JsonDefaults.Options), Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>Flux SSE : les états poussés partent comme événements <see cref="SonNuage.Contracts.Jam.StateEvent"/>, ou une erreur de lecture.</summary>
    private sealed class Stream : System.IO.Stream
    {
        private readonly Channel<object> _items = Channel.CreateUnbounded<object>();
        private byte[] _pending = [];
        private int _at;

        public static Stream Failing(Exception error)
        {
            var s = new Stream();
            s._items.Writer.TryWrite(error);
            return s;
        }

        public void Push(JamStateDto state) =>
            _items.Writer.TryWrite(Encoding.UTF8.GetBytes($"event: {SonNuage.Contracts.Jam.StateEvent}\ndata: {JsonSerializer.Serialize(state, JsonDefaults.Options)}\n\n"));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_at >= _pending.Length)
            {
                switch (await _items.Reader.ReadAsync(ct))
                {
                    case Exception error: throw error;
                    case byte[] bytes: _pending = bytes; _at = 0; break;
                }
            }
            var n = Math.Min(buffer.Length, _pending.Length - _at);
            _pending.AsMemory(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

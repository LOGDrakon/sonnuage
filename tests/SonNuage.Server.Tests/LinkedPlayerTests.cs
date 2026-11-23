using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

/// <summary>Deux onglets reliés : un seul joue, l'autre le pilote, et la lecture se récupère quand il part.</summary>
public class LinkedPlayerTests
{
    private static readonly TrackDto[] Tracks = [Track("A"), Track("B"), Track("C")];

    [Fact]
    public async Task Only_one_tab_plays_and_the_other_drives_it()
    {
        var (a, localA, b, localB, _) = await TwoTabsAsync();

        await a.PlayAsync(Tracks, 1);
        Assert.True(localA.State.IsPlaying);
        Assert.True(b.IsRemote);
        Assert.Equal("B", b.State.Current?.Title);
        Assert.Equal(3, b.State.Queue.Count);

        // Les commandes de l'onglet B s'exécutent dans l'onglet A. B ne lit rien lui-même.
        await b.NextAsync();
        await b.SeekAsync(42);
        Assert.Equal("C", localA.State.Current?.Title);
        Assert.Equal(42, localA.State.PositionSeconds);
        Assert.Equal("C", b.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
        Assert.Empty(localB.State.Queue);

        // Choisir un album dans B le fait jouer dans A.
        await b.PlayAsync([Track("X")], 0);
        Assert.Equal("X", localA.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
    }

    [Fact]
    public async Task A_station_continues_only_on_the_tab_that_plays_it()
    {
        var (a, localA, b, _, _) = await TwoTabsAsync();

        await a.PlayStationAsync("chill", Tracks);
        Assert.Equal("chill", localA.Station);

        await b.PlayStationAsync("party", Tracks);
        Assert.True(b.IsRemote);
        Assert.Null(localA.Station);
    }

    [Fact]
    public async Task Play_here_moves_playback_to_this_tab()
    {
        var (a, localA, b, localB, _) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 0);
        await a.SeekAsync(10);

        await ((IConnectPlayer)b).PlayHereAsync();

        Assert.True(localB.State.IsPlaying);
        Assert.Equal(10, localB.State.PositionSeconds);
        Assert.False(localA.State.IsPlaying);
        Assert.True(a.IsRemote);
        Assert.False(b.IsRemote);
    }

    [Fact]
    public async Task Closing_the_playing_tab_hands_the_queue_over_paused()
    {
        var (a, _, b, localB, bus) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 2);

        await bus.SendAsync(new PlaybackMessage("bye", "tab-a"));

        Assert.False(b.IsRemote);
        Assert.Equal("C", localB.State.Current?.Title);
        Assert.False(localB.State.IsPlaying);
    }

    [Fact]
    public async Task A_new_tab_finds_the_playing_one_instead_of_restoring_its_own_queue()
    {
        var bus = new Bus();
        var localA = new FakePlayer();
        var a = new LinkedPlayer(localA, localA, bus.Join("tab-a"));
        await a.RestoreAsync();
        await a.PlayAsync(Tracks, 0);

        var localB = new FakePlayer();
        var b = new LinkedPlayer(localB, localB, bus.Join("tab-b"));
        await b.RestoreAsync();

        Assert.True(b.IsRemote);
        Assert.False(localB.Restored);
        Assert.Equal(3, b.State.Queue.Count);
    }

    [Fact]
    public void Messages_survive_the_json_trip_through_the_browser_channel()
    {
        var state = PlayerState.Empty with { Queue = Tracks, CurrentIndex = 1, IsPlaying = true, PositionSeconds = 12.5, Repeat = RepeatMode.All };
        var json = System.Text.Json.JsonSerializer.Serialize(new PlaybackMessage("state", "tab-a", State: state, HasQueue: true), JsonDefaults.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<PlaybackMessage>(json, JsonDefaults.Options)!;
        Assert.Equal("B", back.State!.Current?.Title);
        Assert.Equal(RepeatMode.All, back.State.Repeat);
        Assert.Equal(12.5, back.State.PositionSeconds);

        var bye = System.Text.Json.JsonSerializer.Deserialize<PlaybackMessage>("""{"type":"bye","from":"tab-a"}""", JsonDefaults.Options)!;
        Assert.Equal(("bye", "tab-a"), (bye.Type, bye.From));
    }

    [Fact]
    public async Task The_device_that_plays_is_shown_by_its_name()
    {
        var (a, _, b, _, _) = await TwoTabsAsync();

        await a.PlayAsync(Tracks, 0);

        Assert.Equal("Name of tab-a", ((IConnectPlayer)b).RemoteName);
        Assert.Null(((IConnectPlayer)a).RemoteName);
    }

    [Fact]
    public async Task After_a_reconnection_the_playing_device_sends_its_whole_queue_again()
    {
        var (a, _, b, _, bus) = await TwoTabsAsync();
        await a.PlayAsync(Tracks, 0);
        // B a été coupé du relais : il a reçu le « bye » de A et repris la file en pause.
        await bus.SendAsync(new PlaybackMessage("bye", "tab-a"));
        Assert.False(b.IsRemote);

        await bus.ReconnectAsync("tab-a");

        Assert.True(b.IsRemote);
        Assert.Equal(3, b.State.Queue.Count);
    }

    [Fact]
    public async Task The_position_alone_is_sent_at_most_once_a_second()
    {
        var bus = new Bus();
        var time = new Clock();
        var localA = new FakePlayer();
        var a = new LinkedPlayer(localA, localA, bus.Join("tab-a"), time);
        await a.RestoreAsync();
        await a.PlayAsync(Tracks, 0);
        var sent = bus.Sent.Count;

        await localA.SeekAsync(1);
        await localA.SeekAsync(1.5);
        Assert.Equal(sent, bus.Sent.Count);

        time.Now += TimeSpan.FromSeconds(1);
        await localA.SeekAsync(2);
        Assert.Equal(sent + 1, bus.Sent.Count);
        // Tout autre changement part aussitôt.
        await localA.NextAsync();
        Assert.Equal(sent + 2, bus.Sent.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_crossfade_setting_follows_what_the_local_player_can_do(bool supported)
    {
        // Sur le web, Réglages ne montre le fondu enchaîné que si le lecteur relié le gère.
        var local = new FakePlayer { SupportsCrossfade = supported };
        Assert.Equal(supported, new LinkedPlayer(local, local, new Bus().Join("tab")).SupportsCrossfade);
    }

    private sealed class RecordingRoute : IPlayerRoute
    {
        public bool Routes { get; set; } = true;
        public List<string> Calls { get; } = [];
        public Task TogglePlayAsync() { Calls.Add("toggle"); return Task.CompletedTask; }
        public Task SetPlayingAsync(bool playing) { Calls.Add(playing ? "resume" : "pause"); return Task.CompletedTask; }
        public void HoldLocalAudio() => Calls.Add("hold");
        public Task SeekAsync(double seconds) { Calls.Add($"seek {seconds}"); return Task.CompletedTask; }
        public Task NextAsync() { Calls.Add("next"); return Task.CompletedTask; }
        public Task PreviousAsync() { Calls.Add("previous"); return Task.CompletedTask; }
        public Task PlayAsync(TrackDto track) { Calls.Add($"play {track.Title}"); return Task.CompletedTask; }
        public Task EnqueueAsync(IReadOnlyList<TrackDto> tracks) { Calls.Add($"enqueue {string.Join(",", tracks.Select(t => t.Title))}"); return Task.CompletedTask; }
        public PlayerState? Shown { get; set; }
        public event Action? ShownChanged;
        public void Show(PlayerState? state) { Shown = state; ShownChanged?.Invoke(); }
    }

    [Fact]
    public async Task When_the_sound_plays_on_the_host_the_player_shows_the_host()
    {
        // Jam « hôte seulement » : rien ne joue ici, mais le lecteur affiche et pilote ce que joue l'hôte.
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        await player.PlayAsync(Tracks, 0);
        var route = new RecordingRoute();
        player.Route = route;
        var changes = 0;
        player.StateChanged += () => changes++;

        var host = PlayerState.Empty with { Queue = [Tracks[2]], CurrentIndex = 0, IsPlaying = true, PositionSeconds = 12 };
        route.Show(host);

        Assert.Equal(host, player.State);
        Assert.True(changes > 0);
        await player.TogglePlayAsync();
        Assert.Equal(["toggle"], route.Calls);

        route.Show(null);
        Assert.Equal(local.State, player.State);
    }

    [Fact]
    public async Task During_a_jam_the_users_commands_go_to_the_host()
    {
        // Dans un Jam, pause, saut, suivant ou « lire » doivent agir chez l'hôte : sur le lecteur local, la synchro les
        // annulerait aussitôt.
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        await player.PlayAsync(Tracks, 0);
        var route = new RecordingRoute();
        player.Route = route;
        var before = local.State;

        await player.TogglePlayAsync();
        await player.SeekAsync(42);
        await player.NextAsync();
        await player.PreviousAsync();
        await player.PlayAsync(Tracks, 1);
        await player.EnqueueAsync([Tracks[2]], playNext: false);

        Assert.Equal(["toggle", "seek 42", "next", "previous", "play B", "enqueue C"], route.Calls);
        Assert.Equal(before, local.State);
    }

    [Fact]
    public async Task During_a_jam_the_queue_page_acts_on_the_hosts_queue()
    {
        // La file affichée est celle de l'hôte : y toucher un titre le fait jouer chez l'hôte (le suivant : « suivant »),
        // et retirer, mélanger ou répéter ne touchent pas la file locale cachée.
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        await player.PlayAsync(Tracks, 0);
        var route = new RecordingRoute();
        player.Route = route;
        route.Show(PlayerState.Empty with { Queue = [Tracks[2], Tracks[1], Tracks[0]], CurrentIndex = 0, IsPlaying = true });
        var before = local.State;

        await player.JumpToAsync(0);
        await player.JumpToAsync(1);
        await player.JumpToAsync(2);
        await player.RemoveAtAsync(1);
        await player.SetShuffleAsync(true);
        await player.SetRepeatAsync(RepeatMode.One);

        Assert.Equal(["next", "play A"], route.Calls);
        Assert.Equal(before, local.State);
    }

    [Fact]
    public async Task During_a_jam_shuffle_play_starts_one_of_the_tracks_on_the_host()
    {
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        var route = new RecordingRoute();
        player.Route = route;

        await player.PlayAsync(Tracks, -1, shuffle: true);

        var call = Assert.Single(route.Calls);
        Assert.Contains(call, Tracks.Select(t => $"play {t.Title}"));
    }

    [Fact]
    public async Task The_jam_sync_itself_drives_the_local_player()
    {
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        var route = new RecordingRoute();
        player.Route = route;

        await PlayerRouting.BypassAsync(() => player.PlayAsync(Tracks, 0));

        Assert.Empty(route.Calls);
        Assert.Equal("A", local.State.Current?.Title);
        // Le contournement reste propre à la synchro : une commande de l'utilisateur ensuite va toujours à l'hôte.
        await player.NextAsync();
        Assert.Equal(["next"], route.Calls);
    }

    [Fact]
    public async Task Without_an_active_jam_commands_stay_local()
    {
        var local = new FakePlayer();
        var player = new LinkedPlayer(local, local, new Bus().Join("tab"));
        await player.RestoreAsync();
        player.Route = new RecordingRoute { Routes = false };

        await player.PlayAsync(Tracks, 1);

        Assert.Equal("B", local.State.Current?.Title);
    }

    private static async Task<(LinkedPlayer A, FakePlayer LocalA, LinkedPlayer B, FakePlayer LocalB, Bus Bus)> TwoTabsAsync()
    {
        var bus = new Bus();
        var localA = new FakePlayer();
        var localB = new FakePlayer();
        var a = new LinkedPlayer(localA, localA, bus.Join("tab-a"));
        var b = new LinkedPlayer(localB, localB, bus.Join("tab-b"));
        await a.RestoreAsync();
        await b.RestoreAsync();
        return (a, localA, b, localB, bus);
    }

    private static TrackDto Track(string title) => new(
        Guid.NewGuid(), title, Guid.NewGuid(), "Artiste", Guid.NewGuid(), "Album", 1, 1, 200, "mp3", false, 320, false, false);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Relais en mémoire : chaque message part vers tous les autres onglets, aussitôt.</summary>
    private sealed class Bus
    {
        private readonly List<Link> _links = [];

        public Link Join(string id)
        {
            var link = new Link(id, this);
            _links.Add(link);
            return link;
        }

        /// <summary>Comme le relais du serveur : le nom de l'expéditeur est ajouté au message.</summary>
        public List<PlaybackMessage> Sent { get; } = [];

        public async Task SendAsync(PlaybackMessage m)
        {
            Sent.Add(m);
            m = m with { FromName = "Name of " + m.From };
            foreach (var l in _links.ToList())
            {
                if (l.DeviceId == m.From || m.To is { } to && to != l.DeviceId || l.OnMessage is null) continue;
                await l.OnMessage(m);
            }
        }

        public async Task ReconnectAsync(string id)
        {
            if (_links.Single(l => l.DeviceId == id).OnConnected is { } connected) await connected();
        }
    }

    private sealed class Link(string id, Bus bus) : IPlaybackLink
    {
        public string DeviceId => id;
        public Func<PlaybackMessage, Task>? OnMessage { get; set; }
        public Func<Task>? OnConnected { get; set; }
        public Task StartAsync() => Task.CompletedTask;
        public Task SendAsync(PlaybackMessage message) => bus.SendAsync(message);
    }
}

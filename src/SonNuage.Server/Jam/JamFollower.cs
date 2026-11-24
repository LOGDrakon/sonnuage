using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;

namespace SonNuage.Server.Jam;

/// <summary>
/// Suit un Jam Spotify pour un compte : rejoint la session, ouvre le flux « dealer », lit l'état de lecture de l'hôte
/// (connect-state) et, à chaque changement, résout le titre vers une piste SonNuage puis publie l'état (<see
/// cref="JamStateDto"/>). Une instance par compte qui suit un Jam ; <see cref="DisposeAsync"/> l'arrête.
/// </summary>
public sealed class JamFollower : IAsyncDisposable
{
    private static readonly TimeSpan Keepalive = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImportPoll = TimeSpan.FromSeconds(5);

    /// <summary>Taille maximale d'une trame dealer, et de son contenu décompressé : un cluster pèse quelques dizaines de Ko.</summary>
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    private readonly Guid _userId;
    private readonly string _joinToken;
    private readonly JamAudio _audio;
    /// <summary>Le compte peut demander des téléchargements (rôle lu au « join ») : sans ce droit, pas d'import serveur.</summary>
    private readonly bool _mayImport;
    /// <summary>
    /// Imports lancés par ce suivi : garde-fou en plus du quota (un hôte qui saute de titre en titre). Une place est
    /// réservée avant l'import (les préparations tournent en parallèle) et rendue si rien n'est parti : refus du quota,
    /// aucun résultat sûr, vidéo déjà importée.
    /// </summary>
    private int _imports;
    private const int MaxImports = 30;
    private readonly IServiceScopeFactory _scopes;
    /// <summary>Publie l'état : le hub appelle le calcul dans son verrou, pour que les états partent dans l'ordre.</summary>
    private readonly Action<Func<JamStateDto>> _publish;
    private readonly Action<JamFollower> _ended;
    private readonly TaskCompletionSource<bool> _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private readonly Task _loop;

    /// <summary>
    /// Ce qu'on sait de l'hôte, remplacé d'un bloc à chaque état reçu (une seule référence, lue sans verrou depuis les
    /// préparations et les commandes : jamais un titre avec la file d'un autre).
    /// </summary>
    /// <param name="ClockOffsetMs">Notre horloge moins celle de Spotify, estimée comme NTP : à chaque état reçu,
    /// (réception − server_timestamp_ms) vaut décalage + temps de vol ; le minimum observé est la meilleure estimation
    /// (le vol le plus court).</param>
    private sealed record HostView(ClusterState? Cluster, string? TrackId, IReadOnlyList<string> Next, long? ClockOffsetMs);

    private volatile HostView _host = new(null, null, [], null);
    private volatile string? _hostName;

    public JamFollower(Guid userId, string joinToken, JamAudio audio, bool mayImport, IServiceScopeFactory scopes, Action<Func<JamStateDto>> publish,
        Action<JamFollower> ended, ILogger log)
    {
        _userId = userId;
        _joinToken = joinToken;
        _audio = audio;
        _mayImport = mayImport;
        _scopes = scopes;
        _publish = publish;
        _ended = ended;
        _log = log;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public Guid UserId => _userId;
    public JamAudio Audio => _audio;

    /// <summary>Issue du premier « join » : vrai si Spotify nous a acceptés dans le Jam, faux s'il a refusé ou échoué.</summary>
    public Task<bool> Joined => _joined.Task;

    /// <summary>Le dernier état connu (pour un nouvel abonné), ou un état inactif tant que rien n'est reçu.</summary>
    public JamStateDto Current() => Build();

    /// <summary>Comment s'est terminée une connexion au dealer.</summary>
    private enum Outcome { Over, Dropped }

    /// <summary>
    /// Suit le Jam tant qu'il dure : une connexion au dealer, et si elle tombe, une nouvelle (le Jam est rejoint à
    /// chaque fois). Le suivi s'arrête quand le Jam est fini (raison de fin poussée, ou « join » refusé) ou après trop
    /// de connexions ratées d'affilée ; le hub est alors prévenu pour que les appareils le sachent.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Outcome outcome;
                _connectedAtMs = 0;
                try
                {
                    outcome = await FollowOnceAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Coupure, mais aussi délai dépassé (HttpClient, attente de l'id de connexion) : on se reconnecte.
                    _log.LogWarning(ex, "Jam : connexion au dealer perdue pour le compte {User}", _userId);
                    outcome = Outcome.Dropped;
                }
                if (outcome == Outcome.Over || ct.IsCancellationRequested) break;
                // Une connexion qui a tenu remet le compte à zéro ; une qui rejoint puis tombe aussitôt (dealer qui
                // ferme à chaque fois) compte comme un échec, sinon on se reconnecterait chaque seconde sans fin.
                var lived = _connectedAtMs != 0 && Environment.TickCount64 - _connectedAtMs >= StableConnection.TotalMilliseconds;
                failures = lived ? 0 : failures + 1;
                _connected = false;
                if (failures >= Reconnects.Length) { _log.LogWarning("Jam : abandon après {Count} connexions ratées", failures); break; }
                await Task.Delay(Reconnects[failures], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Arrêt demandé.
        }
        finally
        {
            _joined.TrySetResult(false);
            if (!ct.IsCancellationRequested) _ended(this);
        }
    }

    /// <summary>Pauses avant de se reconnecter, selon le nombre d'échecs d'affilée (au-delà : abandon).</summary>
    private static readonly TimeSpan[] Reconnects = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Durée à partir de laquelle une connexion a « tenu » et remet le compte des échecs à zéro.</summary>
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(60);

    /// <summary>Vrai dès qu'une connexion a rejoint le Jam (les commandes sont alors possibles).</summary>
    private volatile bool _connected;

    /// <summary>Moment (TickCount64) où la connexion en cours a rejoint le Jam ; 0 si elle ne l'a pas fait.</summary>
    private long _connectedAtMs;

    /// <summary>
    /// Session rejointe : hôtes spclient (pour les commandes) et identifiant (pour la quitter). L'identifiant peut
    /// manquer si les infos de session n'ont pas répondu : le « join » et les commandes n'en ont pas besoin, il est
    /// redemandé au moment de quitter. Une seule référence, jamais lue à moitié.
    /// </summary>
    private sealed record JoinedSession(SpotifyHosts Hosts, string? Id);

    private volatile JoinedSession? _session;

    /// <summary>Une raison de fin est arrivée (Jam supprimé, exclu…) : la connexion en cours est la dernière.</summary>
    private volatile bool _over;

    /// <summary>Une connexion : jeton frais, appareil caché enregistré, Jam rejoint, puis lecture des push jusqu'à la coupure.</summary>
    private async Task<Outcome> FollowOnceAsync(CancellationToken ct)
    {
        var accessToken = await FreshAccessTokenAsync(ct);
        if (accessToken is null) { _log.LogWarning("Jam : aucun jeton Spotify valide pour le compte {User}", _userId); return Outcome.Over; }

        // Un client HTTP par opération réseau (sans garder de scope DB ouvert pendant tout le suivi).
        SpotifyHosts? hosts;
        JamSessionInfo? info;
        using (var scope = _scopes.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
            hosts = await client.ResolveHostsAsync(ct);
            if (hosts is null) { _log.LogWarning("Jam : hôtes Spotify introuvables"); return Outcome.Dropped; }
            info = await client.SessionInfoAsync(hosts, accessToken, _joinToken, ct);
            _hostName = info?.HostName ?? _hostName;
            // Infos absentes : on rejoint quand même (le refus éventuel vient du « join »), les commandes n'ont besoin
            // que des hôtes. Avant, la session restait vide et les commandes « pas prêt » pendant tout le Jam.
            _session = new JoinedSession(hosts, info?.SessionId ?? _session?.Id);
        }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"wss://{hosts.Dealer}/?access_token={Uri.EscapeDataString(accessToken)}"), ct);
        var connectionId = await ReadConnectionIdAsync(ws, ct);
        if (connectionId is null) { _log.LogWarning("Jam : pas d'identifiant de connexion dealer"); return Outcome.Dropped; }

        using (var scope = _scopes.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
            await client.RegisterReceiverAsync(hosts, accessToken, _deviceId, connectionId, ct);
            // Rejoindre APRÈS l'enregistrement, avec l'id connect réel (hobs_…) : les mises à jour du Jam visent l'appareil
            // membre. Refusé : le Jam est fini (ou on n'y a pas droit), le dealer n'enverrait plus rien.
            if (!await client.JoinAsync(hosts, accessToken, info?.JoinToken ?? _joinToken, $"hobs_{_deviceId}", ct))
            {
                _joined.TrySetResult(false);
                return Outcome.Over;
            }
            _joined.TrySetResult(true);
            _connected = true;
            _connectedAtMs = Environment.TickCount64;
            if (await client.RegisterReceiverAsync(hosts, accessToken, _deviceId, connectionId, ct) is { } initial)
                Process(initial, ct);
        }

        await PumpAsync(ws, ct);
        return _over ? Outcome.Over : Outcome.Dropped;
    }

    /// <summary>Dernière trame reçue du dealer (TickCount64) : le keepalive coupe une connexion devenue muette.</summary>
    private long _lastFrameAtMs;

    /// <summary>Lit le flux dealer : traite « message » (cluster) et « request » (commandes, auxquelles on répond), avec keepalive.</summary>
    private async Task PumpAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Keepalive à part : annuler un ReceiveAsync de ClientWebSocket ABANDONNE la connexion, on ne le coupe donc jamais.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = new SemaphoreSlim(1, 1);
        _sendLock = send;
        Interlocked.Exchange(ref _lastFrameAtMs, Environment.TickCount64);
        var keepalive = Task.Run(() => KeepaliveAsync(ws, pumpCts.Token));
        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                var tooBig = false;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    Interlocked.Exchange(ref _lastFrameAtMs, Environment.TickCount64);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Jam : dealer fermé ({Status})", result.CloseStatus);
                        return;
                    }
                    // Trame démesurée : lue jusqu'au bout pour rester calé sur le flux, mais pas gardée en mémoire.
                    if (!tooBig && ms.Length + result.Count > MaxFrameBytes) { tooBig = true; ms.SetLength(0); }
                    if (!tooBig) ms.Write(buffer.Array!, 0, result.Count);
                } while (!result.EndOfMessage);
                if (tooBig) { _log.LogWarning("Jam : trame dealer de plus de {Max} octets ignorée", MaxFrameBytes); continue; }
                await HandleFrameAsync(ws, ms.ToArray(), ct);
            }
            _log.LogInformation("Jam : dealer terminé (état {State})", ws.State);
        }
        finally
        {
            pumpCts.Cancel();
            await keepalive;
            // Plus personne n'envoie sur cette connexion (le keepalive est fini, les réponses partent de cette boucle).
            _sendLock = null;
            send.Dispose();
        }
    }

    /// <summary>
    /// Ping toutes les 30 s, et vérifie que le dealer vit : rien reçu (pas même le pong) depuis deux périodes, ou ping
    /// impossible à envoyer, et la connexion est coupée pour que la boucle se reconnecte. Sans cela, une connexion à
    /// moitié morte laissait le suivi figé sans jamais se reconnecter.
    /// </summary>
    private async Task KeepaliveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(Keepalive, ct);
                var silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastFrameAtMs);
                if (silentMs > 2 * Keepalive.TotalMilliseconds)
                {
                    _log.LogWarning("Jam : dealer muet depuis {Seconds} s, reconnexion", silentMs / 1000);
                    ws.Abort();
                    return;
                }
                try
                {
                    await SendAsync(ws, Ping, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning("Jam : ping du dealer impossible ({Error}), reconnexion", ex.Message);
                    ws.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private const string Ping = "{\"type\":\"ping\"}";

    private volatile SemaphoreSlim? _sendLock;

    private async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var gate = _sendLock;
        if (gate is not null) await gate.WaitAsync(ct);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { gate?.Release(); }
    }

    /// <summary>
    /// Traite une trame dealer selon son type (voir go-librespot) : message (push), request (commande à acquitter), pong.
    /// Une trame illisible (JSON, base64, gzip corrompu…) est journalisée et sautée : elle ne coupe pas la connexion.
    /// </summary>
    private async Task HandleFrameAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
    {
        string? type = null, key = null;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            type = Str(root, "type");
            var uri = Str(root, "uri");
            key = Str(root, "key");
            if (type == "message" && uri is not null && DecodeMessagePayload(root) is { } payload)
            {
                if (uri.StartsWith("hm://connect-state/v1/cluster", StringComparison.Ordinal))
                {
                    if (ClusterState.ParseClusterUpdate(payload) is { } cluster) Process(cluster, ct);
                }
                else if (uri.StartsWith(SessionUpdate.Uri, StringComparison.Ordinal))
                {
                    var reason = SessionUpdate.Reason(payload);
                    _log.LogInformation("Jam : session mise à jour ({Reason})", reason ?? "?");
                    if (SessionUpdate.EndsJam(payload))
                    {
                        // Fini pour nous : on ferme, la boucle de suivi verra _over et ne se reconnectera pas.
                        _over = true;
                        ws.Abort();
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation("Jam : trame dealer ignorée ({Error})", ex.Message);
            return;
        }

        // Les « request » (commandes de lecture visant cet appareil) doivent être acquittées, sinon le dealer peut cesser d'émettre.
        if (type == "request" && key is not null)
        {
            // Construite, pas interpolée : la clé vient du réseau.
            var reply = new JsonObject { ["type"] = "reply", ["key"] = key, ["payload"] = new JsonObject { ["success"] = true } }.ToJsonString();
            try { await SendAsync(ws, reply, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Connexion en train de tomber : le keepalive (ou la réception) s'en charge.
                _log.LogDebug("Jam : réponse au dealer impossible ({Error})", ex.Message);
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Décode le payload d'une trame « message » : payloads[0] base64, décompressé si Transfer-Encoding gzip (ou magie gzip).</summary>
    private static byte[]? DecodeMessagePayload(JsonElement root)
    {
        if (!root.TryGetProperty("payloads", out var pl) || pl.ValueKind != JsonValueKind.Array || pl.GetArrayLength() == 0) return null;
        var first = pl[0];
        byte[] bytes;
        try { bytes = first.ValueKind == JsonValueKind.String ? Convert.FromBase64String(first.GetString()!) : Encoding.UTF8.GetBytes(first.GetRawText()); }
        catch (FormatException) { return null; }
        return Gunzip(bytes);
    }

    /// <summary>Décompresse si c'est du gzip, au plus <see cref="MaxFrameBytes"/> (une bombe gzip ne remplit pas la mémoire).</summary>
    internal static byte[] Gunzip(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = gz.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (output.Length + n > MaxFrameBytes) throw new InvalidDataException($"payload décompressé de plus de {MaxFrameBytes} octets");
            output.Write(chunk, 0, n);
        }
        return output.ToArray();
    }

    private async Task<string?> ReadConnectionIdAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[32 * 1024]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 5; i++)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, timeout.Token);
                if (ms.Length + result.Count > MaxFrameBytes) return null;
                ms.Write(buffer.Array!, 0, result.Count);
            } while (!result.EndOfMessage);
            try
            {
                using var doc = JsonDocument.Parse(ms.ToArray());
                if (doc.RootElement.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
                    && h.TryGetProperty("Spotify-Connection-Id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>Traite un nouvel état de l'hôte : si le titre a changé, (re)résout ; sinon met à jour position/pause.</summary>
    private void Process(ClusterState cluster, CancellationToken ct)
    {
        var previous = _host;
        // Arrêté, l'hôte peut ne plus annoncer d'appareil actif : on garde le dernier, cible de « reprendre ».
        if (cluster.ActiveDeviceId is not { Length: > 0 } && previous.Cluster?.ActiveDeviceId is { Length: > 0 } device)
            cluster = cluster with { ActiveDeviceId = device };
        var offset = previous.ClockOffsetMs;
        if (cluster.ServerTimestampMs > 0)
        {
            var sample = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cluster.ServerTimestampMs;
            if (offset is not { } best || sample < best) offset = sample;
        }
        // Pub, épisode, fichier local : pas un titre à suivre, le précédent n'est plus le titre en cours.
        var trackId = cluster.TrackId;
        // File de l'hôte : on prépare d'avance les prochains titres pour que le passage soit immédiat.
        var next = (cluster.NextTrackUris ?? [])
            .Select(u => u.StartsWith("spotify:track:", StringComparison.Ordinal) ? u["spotify:track:".Length..] : null)
            .OfType<string>().Where(id => id != trackId).Distinct().Take(PrefetchAhead).ToList();
        _host = new HostView(cluster, trackId, next, offset);
        if (trackId is not null) Prepare(trackId, cluster.DurationMs / 1000.0, ct);
        foreach (var n in next) Prepare(n, 0, ct);
        Publish();
    }

    /// <summary>Titres suivants préparés d'avance (métadonnées, bibliothèque, import YouTube) pour un démarrage immédiat.</summary>
    private const int PrefetchAhead = 2;
    private const int PrepCacheSize = 40;
    private static readonly TimeSpan ImportGiveUp = TimeSpan.FromMinutes(4);

    /// <summary>Préparation d'un titre Spotify : où en est sa résolution en un titre jouable ici.</summary>
    private sealed class Prep
    {
        public volatile JamTrackDto? Meta;
        public volatile TrackDto? Resolved;
        /// <summary>Écrit après <see cref="Resolved"/> : qui lit « Matched » trouve la piste.</summary>
        public volatile JamResolve State = JamResolve.Pending;
        public long Order;
        /// <summary>Échec passager (délai, réseau, import abandonné) : à retenter après ce moment.</summary>
        public DateTimeOffset? RetryAfter;
    }

    private readonly ConcurrentDictionary<string, Prep> _preps = new();
    private long _prepOrder;

    /// <summary>Lance (une fois) la préparation d'un titre ; rend son suivi.</summary>
    private Prep Prepare(string trackId, double durationSeconds, CancellationToken ct)
    {
        if (_preps.TryGetValue(trackId, out var existing))
        {
            if (existing.RetryAfter is not { } retry || retry > DateTimeOffset.UtcNow) return existing;
            // Échoué pour une raison passagère : on retente au lieu de garder l'échec pour tout le Jam.
            _preps.TryRemove(new KeyValuePair<string, Prep>(trackId, existing));
        }
        var prep = new Prep { Order = Interlocked.Increment(ref _prepOrder) };
        if (!_preps.TryAdd(trackId, prep)) return _preps[trackId];
        // Le cache reste petit : on oublie les plus anciennes préparations (sauf celle en cours).
        var current = _host.TrackId;
        if (_preps.Count > PrepCacheSize)
            foreach (var old in _preps.Where(p => p.Key != current).OrderBy(p => p.Value.Order).Take(_preps.Count - PrepCacheSize).ToList())
                _preps.TryRemove(old.Key, out _);
        _ = Task.Run(() => RunPrepAsync(trackId, durationSeconds, prep, ct), ct);
        return prep;
    }

    private async Task RunPrepAsync(string trackId, double durationSeconds, Prep prep, CancellationToken ct)
    {
        try
        {
            using (var scope = _scopes.CreateScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
                // Jeton frais à chaque titre : celui de la connexion expire au bout d'une heure, qu'elle peut dépasser.
                var accessToken = await FreshAccessTokenAsync(ct);
                var meta = accessToken is null ? null : await client.TrackAsync(_session?.Hosts, accessToken, trackId, ct);
                // Durée : celle du cluster si connue (celle que joue l'hôte).
                prep.Meta = meta is null ? null : durationSeconds > 0 ? meta with { DurationSeconds = durationSeconds } : meta;
            }
            if (prep.Meta is not { } wanted) { Fail(trackId, prep, RetrySoon); return; }
            Changed(trackId);

            string? videoId;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<JamResolver>();
                if (await resolver.FindInLibraryAsync(db, _userId, wanted, ct) is { } inLibrary)
                {
                    SetPrep(trackId, prep, JamResolve.Matched, inLibrary);
                    return;
                }
                // Son chez l'invité : import YouTube pour pouvoir jouer. En « distant seul », inutile.
                // Pas le droit d'importer, ou trop d'imports pour ce Jam : introuvable ici, l'appareil joue YouTube en direct.
                if (_audio != JamAudio.Guest || !_mayImport) { SetPrep(trackId, prep, JamResolve.Unavailable, null); return; }
                if (Interlocked.Increment(ref _imports) > MaxImports)
                {
                    Interlocked.Decrement(ref _imports);
                    SetPrep(trackId, prep, JamResolve.Unavailable, null);
                    return;
                }
                // Refus du quota (ou du rôle, relu en base) : null, retenté plus tard comme un import raté.
                (string VideoId, bool Queued)? started = null;
                try
                {
                    started = await resolver.StartImportAsync(db, _userId, wanted, ct);
                }
                finally
                {
                    if (started is not { Queued: true }) Interlocked.Decrement(ref _imports);
                }
                videoId = started?.VideoId;
            }
            if (videoId is null) { Fail(trackId, prep, RetryLater); return; }
            SetPrep(trackId, prep, JamResolve.Importing, null);

            var deadline = DateTimeOffset.UtcNow + ImportGiveUp;
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(ImportPoll, ct);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SonNuageDbContext>();
                var resolver = scope.ServiceProvider.GetRequiredService<JamResolver>();
                var outcome = await resolver.CheckImportAsync(db, _userId, videoId, ct);
                if (outcome.State == JamResolve.Matched) { SetPrep(trackId, prep, JamResolve.Matched, outcome.Track); return; }
                if (outcome.State == JamResolve.Unavailable) { Fail(trackId, prep, RetryLater); return; }
            }
            Fail(trackId, prep, RetryLater); // Import trop long : abandonné pour l'instant.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Jam : préparation du titre {Track} impossible", trackId);
            Fail(trackId, prep, RetrySoon);
        }
    }

    /// <summary>Délais avant de retenter un titre : métadonnées ou erreur passagère, puis recherche ou import ratés.</summary>
    private static readonly TimeSpan RetrySoon = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryLater = TimeSpan.FromMinutes(10);

    /// <summary>Introuvable pour l'instant : publié comme tel, et retenté si l'hôte rejoue le titre plus tard.</summary>
    private void Fail(string trackId, Prep prep, TimeSpan retryIn)
    {
        prep.RetryAfter = DateTimeOffset.UtcNow + retryIn;
        SetPrep(trackId, prep, JamResolve.Unavailable, null);
    }

    private void SetPrep(string trackId, Prep prep, JamResolve state, TrackDto? resolved)
    {
        _log.LogDebug("Jam : titre {Track} → {State}", trackId, state);
        prep.Resolved = resolved;
        prep.State = state;
        Changed(trackId);
    }

    /// <summary>Une préparation a avancé : republie si elle concerne le titre en cours ou un des suivants.</summary>
    private void Changed(string trackId)
    {
        var host = _host;
        if (trackId == host.TrackId || host.Next.Contains(trackId)) Publish();
    }

    /// <summary>Un seul rafraîchissement à la fois : deux avec le même jeton de rafraîchissement, et Spotify refuse le
    /// second (ou en enregistre un déjà remplacé).</summary>
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>
    /// Jeton d'accès frais : rafraîchi et réenregistré s'il expire bientôt. Relu en base une fois la main obtenue : un
    /// appel qui attendait trouve le jeton que le précédent vient d'enregistrer.
    /// </summary>
    private async Task<string?> FreshAccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            return await RefreshIfNeededAsync(ct);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<string?> RefreshIfNeededAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SonNuageDbContext>();
        var tokens = sp.GetRequiredService<SpotifyTokens>();
        var client = sp.GetRequiredService<SpotifyClient>();
        var row = await db.Users.Where(u => u.Id == _userId)
            .Select(u => new { u.SpotifyAccessToken, u.SpotifyRefreshToken, u.SpotifyExpiresAt, u.SpotifyDisplayName })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var access = tokens.Read(row.SpotifyAccessToken);
        if (access is not null && row.SpotifyExpiresAt is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(1)) return access;

        var refresh = tokens.Read(row.SpotifyRefreshToken);
        if (refresh is null) return access; // Pas de rafraîchissement : on tente le jeton existant.
        // Null si refusé ou Spotify injoignable : on tente le jeton existant, le suivant réessaiera.
        var fresh = await client.RefreshAsync(refresh, ct);
        if (fresh is null) return access;
        await tokens.SaveAsync(db, _userId, fresh.AccessToken, fresh.RefreshToken ?? refresh, fresh.ExpiresAt, row.SpotifyDisplayName, ct);
        return fresh.AccessToken;
    }

    private void Publish() => _publish(Build);

    private JamStateDto Build()
    {
        var now = DateTimeOffset.UtcNow;
        var host = _host;
        var current = host.TrackId is { } id && _preps.TryGetValue(id, out var p) ? p : null;
        var upcoming = host.Next
            .Select(n => _preps.TryGetValue(n, out var np) && np.State == JamResolve.Matched ? np.Resolved : null)
            .TakeWhile(t => t is not null).OfType<TrackDto>().ToList();
        var state = current?.State ?? JamResolve.Pending;
        return new JamStateDto(
            Active: true,
            Service: Contracts.Jam.Spotify,
            Audio: _audio,
            HostName: _hostName,
            Track: current?.Meta ?? (host.Cluster is { TrackId: not null } c ? new JamTrackDto(c.Title ?? "", "", null, c.DurationMs / 1000.0, null) : null),
            IsPlaying: host.Cluster is { IsPaused: false },
            PositionSeconds: host.Cluster?.PositionSecondsAt(now, host.ClockOffsetMs ?? 0) ?? 0,
            AsOfUnixMs: now.ToUnixTimeMilliseconds(),
            Resolve: state,
            Resolved: state == JamResolve.Matched ? current?.Resolved : null,
            Upcoming: upcoming,
            SourceTrackId: host.TrackId);
    }

    /// <summary>Arrêt en cours ou fini : le hub et la fin du suivi peuvent l'appeler tous deux, il n'a lieu qu'une fois.</summary>
    private TaskCompletionSource? _disposal;

    public async ValueTask DisposeAsync()
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposal, mine, null) is { } running)
        {
            await running.Task;
            return;
        }
        try
        {
            _cts.Cancel();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // Déjà journalisé.
            }
            _cts.Dispose();
            if (!_over) await LeaveSessionAsync();
        }
        finally
        {
            mine.TrySetResult();
        }
    }

    /// <summary>
    /// Envoie une commande au lecteur de l'hôte. Pas encore rejoint ou pas d'appareil actif chez l'hôte : NotReady ;
    /// refusée par Spotify (droits du Jam) : Refused.
    /// </summary>
    public async Task<JamCommandResult> SendAsync(JsonObject command, CancellationToken ct)
    {
        if (!_connected || _session is not { } session || _host.Cluster?.ActiveDeviceId is not { Length: > 0 } host) return JamCommandResult.NotReady;
        var accessToken = await FreshAccessTokenAsync(ct);
        if (accessToken is null) return JamCommandResult.NotReady;
        using var scope = _scopes.CreateScope();
        var status = await scope.ServiceProvider.GetRequiredService<SpotifyClient>()
            .CommandAsync(session.Hosts, accessToken, $"hobs_{_deviceId}", host, command, ct);
        return status is >= 200 and < 300 ? JamCommandResult.Sent : JamCommandResult.Refused;
    }

    /// <summary>
    /// Trouve un titre de SonNuage sur Spotify et l'ajoute à la file de l'hôte, ou le lance aussitôt si
    /// <paramref name="playNow"/>. NotOnSpotify si aucun résultat n'est sûr.
    /// </summary>
    public async Task<JamCommandResult> QueueAsync(JamTrackDto track, bool playNow, CancellationToken ct)
    {
        if (!_connected || _session is null) return JamCommandResult.NotReady;
        var accessToken = await FreshAccessTokenAsync(ct);
        if (accessToken is null) return JamCommandResult.NotReady;
        IReadOnlyList<SpotifySearchHit> hits;
        using (var scope = _scopes.CreateScope())
            hits = await scope.ServiceProvider.GetRequiredService<SpotifyClient>()
                .SearchAsync(accessToken, $"{track.Title} {track.Artist}".Trim(), ct);
        // Le fichier de SonNuage peut venir d'un clip : tolérance de durée d'une vidéo.
        var best = JamMatch.Best(track, hits, h => new JamCandidate(h.Track.Title, h.Track.Artist, h.Track.DurationSeconds, h.Track.Isrc, JamSourceKind.Video));
        _log.LogInformation("Jam : « {Title} » — {Artist} vers Spotify : {Count} résultat(s) → {Chosen}",
            track.Title, track.Artist, hits.Count, best is { } b ? $"« {b.Item.Track.Title} » — {b.Item.Track.Artist} ({b.Verdict.Reason}, {b.Verdict.Score})" : "aucun sûr");
        if (best is not { } chosen) return JamCommandResult.NotOnSpotify;
        return await SendAsync(playNow ? SpotifyClient.PlayCommand(chosen.Item.Uri) : SpotifyClient.QueueCommand(chosen.Item.Uri), ct);
    }

    /// <summary>Quitte le Jam côté Spotify (au mieux, sans bloquer l'arrêt) : on cesse d'y apparaître comme membre.</summary>
    private async Task LeaveSessionAsync()
    {
        if (_session is not { } session) return;
        try
        {
            using var timeout = new CancellationTokenSource(LeaveTimeout);
            var accessToken = await FreshAccessTokenAsync(timeout.Token);
            if (accessToken is null) return;
            using var scope = _scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<SpotifyClient>();
            // Identifiant inconnu (infos de session sans réponse au « join ») : redemandé maintenant.
            var id = session.Id ?? (await client.SessionInfoAsync(session.Hosts, accessToken, _joinToken, timeout.Token))?.SessionId;
            if (id is null) { _log.LogInformation("Jam : session à quitter introuvable côté Spotify"); return; }
            await client.LeaveAsync(session.Hosts, accessToken, id, timeout.Token);
        }
        catch (Exception ex)
        {
            // Tout, même une réponse illisible (JsonException) ou la base : l'échec ne doit pas remonter par
            // DisposeAsync jusqu'à /leave ou /join.
            _log.LogInformation("Jam : impossible de quitter la session côté Spotify ({Error})", ex.Message);
        }
    }

    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(5);
}

public enum JamCommandResult
{
    Sent,
    NotReady,
    Refused,
    /// <summary>Le titre de SonNuage n'a pas d'équivalent sûr sur Spotify.</summary>
    NotOnSpotify,
}

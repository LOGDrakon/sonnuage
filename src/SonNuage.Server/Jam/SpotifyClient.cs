using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using SonNuage.Contracts;

namespace SonNuage.Server.Jam;

/// <summary>Jeton d'accès rafraîchi : valeur et nouvelle expiration (et le jeton de rafraîchissement s'il a changé).</summary>
public sealed record SpotifyRefresh(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken);

/// <summary>Hôtes d'API Spotify résolus dynamiquement (apresolve) : façade « spclient » et aiguilleur « dealer ».</summary>
public sealed record SpotifyHosts(string SpClient, string Dealer);

/// <summary>Un titre trouvé par une recherche Spotify.</summary>
public sealed record SpotifySearchHit(string Uri, JamTrackDto Track);

/// <summary>Un membre d'un Jam.</summary>
public sealed record JamMember(string Id, string DisplayName, bool IsOwner);

/// <summary>Informations d'un Jam lues avant de le rejoindre.</summary>
public sealed record JamSessionInfo(string SessionId, string JoinToken, string? HostName, bool Active, IReadOnlyList<JamMember> Members);

/// <summary>
/// Accès bas niveau à Spotify pour suivre un Jam : rafraîchissement du jeton, métadonnées d'un titre (API Web
/// publique), et endpoints internes « social-connect » / « connect-state » (façade spclient). Le son n'est jamais
/// demandé. Le client id est celui de l'appli de bureau, seul accepté par social-connect — hors des CGU de Spotify,
/// la fonctionnalité est donc expérimentale et peut cesser de marcher si Spotify change son API.
/// </summary>
public sealed class SpotifyClient(HttpClient http, ILogger<SpotifyClient> log)
{
    /// <summary>Client id public de l'appli de bureau Spotify (comme librespot), seul reconnu par social-connect.</summary>
    public const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    /// <summary>Client HTTP sous-jacent, pour résoudre un lien court de partage (<see cref="SpotifyLink"/>).</summary>
    public HttpClient Http => http;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Échange un jeton de rafraîchissement contre un jeton d'accès frais.</summary>
    public async Task<SpotifyRefresh?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        });
        using var res = await http.PostAsync("https://accounts.spotify.com/api/token", body, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Rafraîchissement du jeton Spotify refusé : {Status}", (int)res.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at)) return null;
        var expires = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        return new SpotifyRefresh(at.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expires), newRefresh);
    }

    /// <summary>Nom affiché et identifiant du compte (<c>/v1/me</c>), pour confirmer la liaison.</summary>
    public async Task<string?> DisplayNameAsync(string accessToken, CancellationToken ct)
    {
        using var req = Get("https://api.spotify.com/v1/me", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        return doc.RootElement.TryGetProperty("display_name", out var n) ? n.GetString() : null;
    }

    /// <summary>Hôtes spclient + dealer à utiliser pour ce compte.</summary>
    public async Task<SpotifyHosts?> ResolveHostsAsync(CancellationToken ct)
    {
        using var res = await http.GetAsync("https://apresolve.spotify.com/?type=spclient&type=dealer", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        var spclient = First(root, "spclient");
        var dealer = First(root, "dealer");
        if (spclient is null || dealer is null) return null;
        return new SpotifyHosts($"https://{Strip(spclient)}", Strip(dealer));

        static string? First(JsonElement root, string key) =>
            root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 ? a[0].GetString() : null;
        static string Strip(string host) => host.EndsWith(":443", StringComparison.Ordinal) ? host[..^4] : host;
    }

    /// <summary>
    /// Métadonnées d'un titre : d'abord celles de l'appli Spotify (<c>metadata/4/track</c> sur spclient, avec l'ISRC
    /// qui identifie l'enregistrement), sinon la page d'intégration publique (titre et artistes seulement).
    /// </summary>
    public async Task<JamTrackDto?> TrackAsync(SpotifyHosts? hosts, string accessToken, string trackId, CancellationToken ct)
    {
        if (hosts is not null && TrackGid(trackId) is { } gid)
        {
            try
            {
                using var req = Get($"{hosts.SpClient}/metadata/4/track/{gid}?market=from_token", accessToken);
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode && ParseTrackMetadata(await res.Content.ReadAsByteArrayAsync(ct)) is { } meta) return meta;
                log.LogDebug("Métadonnées Spotify de {Track} : {Status}, page d'intégration en secours", trackId, (int)res.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                log.LogDebug(ex, "Métadonnées Spotify de {Track} indisponibles, page d'intégration en secours", trackId);
            }
        }
        return await EmbedTrackAsync(trackId, ct);
    }

    /// <summary>Identifiant base62 d'un titre (22 caractères) en gid hexadécimal (16 octets) ; null s'il est invalide.</summary>
    public static string? TrackGid(string trackId)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (trackId.Length != 22) return null;
        var n = System.Numerics.BigInteger.Zero;
        foreach (var c in trackId)
        {
            var digit = alphabet.IndexOf(c);
            if (digit < 0) return null;
            n = n * 62 + digit;
        }
        var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 16) return null;
        return Convert.ToHexStringLower(new byte[16 - bytes.Length].Concat(bytes).ToArray());
    }

    /// <summary>Réponse JSON de <c>metadata/4/track</c> : nom, artistes, album, durée (ms), ISRC. Null si illisible.</summary>
    public static JamTrackDto? ParseTrackMetadata(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "name") is not { Length: > 0 } name) return null;
            var artists = root.TryGetProperty("artist", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? ar.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => Text(a, "name")).Where(n => !string.IsNullOrEmpty(n))
                : [];
            var album = root.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object ? Text(al, "name") : null;
            var duration = root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() / 1000.0 : 0;
            string? isrc = null;
            if (root.TryGetProperty("external_id", out var ids) && ids.ValueKind == JsonValueKind.Array)
                isrc = ids.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.Object && string.Equals(Text(i, "type"), "isrc", StringComparison.OrdinalIgnoreCase))
                    .Select(i => Text(i, "id")).FirstOrDefault(i => !string.IsNullOrEmpty(i));
            return new JamTrackDto(name, string.Join(", ", artists), album, duration, isrc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Titre et artistes via la page d'intégration publique <c>open.spotify.com/embed/track/{id}</c> : sans
    /// authentification ni limite de débit de l'API Web. Pas d'ISRC.
    /// </summary>
    private async Task<JamTrackDto?> EmbedTrackAsync(string trackId, CancellationToken ct)
    {
        using var res = await http.GetAsync($"https://open.spotify.com/embed/track/{Uri.EscapeDataString(trackId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        var html = await res.Content.ReadAsStringAsync(ct);
        var start = html.IndexOf("__NEXT_DATA__", StringComparison.Ordinal);
        if (start < 0) return null;
        start = html.IndexOf('>', start);
        var end = start < 0 ? -1 : html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(html[(start + 1)..end]);
            if (FindEntity(doc.RootElement) is not { } entity) return null;
            var title = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var artist = entity.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ar.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null).Where(s => !string.IsNullOrEmpty(s)))
                : "";
            if (string.IsNullOrEmpty(title)) return null;
            var duration = entity.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() / 1000.0 : 0;
            return new JamTrackDto(title, artist, null, duration, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Repère l'objet du titre dans le __NEXT_DATA__ de la page d'intégration : un objet avec « name » et « artists ».</summary>
    private static JsonElement? FindEntity(JsonElement node, int depth = 0)
    {
        if (depth > 12) return null;
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("name", out _) && node.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array) return node;
                foreach (var p in node.EnumerateObject())
                    if (FindEntity(p.Value, depth + 1) is { } found) return found;
                return null;
            case JsonValueKind.Array:
                foreach (var e in node.EnumerateArray())
                    if (FindEntity(e, depth + 1) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    /// <summary>Informations du Jam désigné par un jeton de partage, ou null s'il n'existe pas / plus.</summary>
    public async Task<JamSessionInfo?> SessionInfoAsync(SpotifyHosts hosts, string accessToken, string joinToken, CancellationToken ct)
    {
        using var req = Get($"{hosts.SpClient}/social-connect/v2/sessions/info/{Uri.EscapeDataString(joinToken)}", accessToken);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
        var root = doc.RootElement;
        var sessionId = Text(root, "session_id") ?? joinToken;
        var token = Text(root, "join_session_token") ?? joinToken;
        var hostName = root.TryGetProperty("host_device_info", out var hi) && hi.ValueKind == JsonValueKind.Object ? Text(hi, "device_name") : null;
        var active = root.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True;
        var ownerId = Text(root, "session_owner_id");
        var members = new List<JamMember>();
        if (root.TryGetProperty("session_members", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
            {
                var id = Text(m, "id") ?? "";
                members.Add(new JamMember(id, Text(m, "display_name") ?? "", id == ownerId));
            }
        // Le nom du compte hôte (« LOG_Drakon ») parle plus que celui de son appareil ; l'appareil en secours.
        if (members.FirstOrDefault(m => m.IsOwner)?.DisplayName is { Length: > 0 } owner) hostName = owner;
        return new JamSessionInfo(sessionId, token, hostName, active, members);
    }

    /// <summary>Rejoint le Jam en écoute + contrôle. Renvoie vrai si accepté (200), faux sinon (403 non autorisé…).</summary>
    public async Task<bool> JoinAsync(SpotifyHosts hosts, string accessToken, string joinToken, string localDeviceId, CancellationToken ct)
    {
        // « control » = écouter sur l'appareil de l'hôte (permis aux comptes gratuits) ; « listen_and_control » exige Premium.
        foreach (var mode in new[] { "control", "listen_and_control" })
        {
            var url = $"{hosts.SpClient}/social-connect/v2/sessions/join/{Uri.EscapeDataString(joinToken)}" +
                $"?playback_control={mode}&join_type=deeplinking&local_device_id={Uri.EscapeDataString(localDeviceId)}" +
                $"&join_attempt_id={Guid.NewGuid():N}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) { log.LogInformation("Jam rejoint ({Mode})", mode); return true; }
            log.LogWarning("Rejoindre le Jam refusé ({Mode}) : {Status} {Body}", mode, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        return false;
    }

    /// <summary>
    /// Quitte le Jam côté Spotify (comme « Quitter » dans l'appli), pour ne pas y rester membre fantôme. Rend vrai si
    /// Spotify l'a accepté.
    /// </summary>
    public async Task<bool> LeaveAsync(SpotifyHosts hosts, string accessToken, string sessionId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{hosts.SpClient}/social-connect/v3/sessions/{Uri.EscapeDataString(sessionId)}/leave");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        log.LogInformation("Jam quitté : {Status}", (int)res.StatusCode);
        return res.IsSuccessStatusCode;
    }

    /// <summary>
    /// Cherche des titres sur Spotify : d'abord la recherche du lecteur web (GraphQL « pathfinder », opération
    /// searchTracks), sinon l'API Web (souvent limitée en débit pour ce client id). Les résultats sont bruts : c'est
    /// <see cref="JamMatch"/> qui décide lequel est le bon.
    /// </summary>
    public async Task<IReadOnlyList<SpotifySearchHit>> SearchAsync(string accessToken, string query, CancellationToken ct)
    {
        var hash = await SearchTracksHashAsync(ct);
        var pathfinder = new HttpRequestMessage(HttpMethod.Post, "https://api-partner.spotify.com/pathfinder/v2/query")
        {
            Content = new StringContent(new JsonObject
            {
                ["variables"] = new JsonObject
                {
                    ["searchTerm"] = query, ["offset"] = 0, ["limit"] = 10, ["numberOfTopResults"] = 5,
                    ["includeAudiobooks"] = false, ["includePreReleases"] = false,
                },
                ["operationName"] = "searchTracks",
                ["extensions"] = new JsonObject { ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = hash } },
            }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        pathfinder.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        pathfinder.Headers.TryAddWithoutValidation("App-Platform", "WebPlayer");
        foreach (var req in new[] { pathfinder, Get($"https://api.spotify.com/v1/search?type=track&limit=10&q={Uri.EscapeDataString(query)}", accessToken) })
        {
            var source = req.RequestUri!.Host;
            try
            {
                using var _ = req;
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var hits = ParseSearch(await res.Content.ReadAsByteArrayAsync(ct));
                    log.LogInformation("Jam : recherche Spotify ({Source}) « {Query} » : {Count} titre(s)", source, query, hits.Count);
                    return hits;
                }
                log.LogInformation("Jam : recherche Spotify ({Source}) : {Status}", source, (int)res.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                log.LogInformation("Jam : recherche Spotify ({Source}) impossible : {Error}", source, ex.Message);
            }
        }
        return [];
    }

    /// <summary>Hash connu de la requête searchTracks (lecteur web de novembre 2026), si la découverte échoue.</summary>
    private const string KnownSearchTracksHash = "b02683192a98dde7966b5e6655a79eeb62713eab703eda9902c932818dd52751";
    private static readonly TimeSpan SearchHashLifetime = TimeSpan.FromHours(12);
    private static (string Hash, DateTimeOffset At)? _searchHash;

    /// <summary>
    /// Le hash de la requête searchTracks change à chaque déploiement du lecteur web : on le relit dans son code public
    /// (page d'accueil → script principal → morceau « xpui-routes-search »), gardé 12 h. En échec, le hash connu.
    /// </summary>
    private async Task<string> SearchTracksHashAsync(CancellationToken ct)
    {
        if (_searchHash is { } cached && DateTimeOffset.UtcNow - cached.At < SearchHashLifetime) return cached.Hash;
        string? found = null;
        var trail = "accueil";
        try
        {
            const string cdn = "https://open.spotifycdn.com/cdn/build/web-player/";
            var home = await PageAsync("https://open.spotify.com/", ct);
            var main = Regex.Match(home, @"https://open\.spotifycdn\.com/cdn/build/web-player/web-player\.[0-9a-f]+\.js");
            trail = main.Success ? "script principal" : $"accueil sans script principal ({home.Length} car.)";
            if (main.Success)
            {
                var code = await PageAsync(main.Value, ct);
                trail = $"script principal ({code.Length} car.), morceaux {string.Join("/", FindChunkHashes(code, "xpui-routes-search"))}";
                found = FindQueryHash(code, "searchTracks");
                foreach (var chunk in found is null ? FindChunkHashes(code, "xpui-routes-search") : [])
                {
                    if (FindQueryHash(await PageAsync($"{cdn}xpui-routes-search.{chunk}.js", ct), "searchTracks") is { } h) { found = h; break; }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            log.LogInformation("Jam : hash de recherche Spotify introuvable ({Error}), hash connu utilisé", ex.Message);
        }
        if (found is null) log.LogInformation("Jam : hash de recherche Spotify non trouvé dans le lecteur web ({Trail}), hash connu utilisé", trail);
        var hash = found ?? KnownSearchTracksHash;
        _searchHash = (hash, DateTimeOffset.UtcNow);
        return hash;
    }

    /// <summary>Page ou script du lecteur web, demandé comme un navigateur (sans user agent, la page d'accueil diffère) ; vide en échec.</summary>
    private async Task<string> PageAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct) : "";
    }

    /// <summary>Hash d'une requête GraphQL persistée dans le code du lecteur web (<c>("searchTracks","query","…")</c>).</summary>
    public static string? FindQueryHash(string code, string operation)
    {
        var m = Regex.Match(code, $@"""{Regex.Escape(operation)}"",""query"",""([0-9a-f]{{64}})""");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Hashes possibles du fichier d'un morceau de code nommé (numéro → nom, puis numéro → hash, JS et CSS).</summary>
    public static IReadOnlyList<string> FindChunkHashes(string code, string chunkName)
    {
        var id = Regex.Match(code, $@"(\d+):""{Regex.Escape(chunkName)}""");
        if (!id.Success) return [];
        return Regex.Matches(code, $@"[{{,]{id.Groups[1].Value}:""([0-9a-f]{{8}})""").Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>Résultats de pathfinder (<c>data.searchV2.tracksV2.items</c>) ou de l'API Web (<c>tracks.items</c>) ; titres seulement.</summary>
    public static IReadOnlyList<SpotifySearchHit> ParseSearch(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            JsonElement list = default;
            var found = root.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object && tr.TryGetProperty("items", out list);
            if (!found && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("searchV2", out var sv) && sv.ValueKind == JsonValueKind.Object
                && sv.TryGetProperty("tracksV2", out var tv) && tv.ValueKind == JsonValueKind.Object && tv.TryGetProperty("items", out var pitems)
                && pitems.ValueKind == JsonValueKind.Array)
                return pitems.EnumerateArray().Select(PathfinderHit).OfType<SpotifySearchHit>().ToList();
            if (!found || list.ValueKind != JsonValueKind.Array) return [];
            var hits = new List<SpotifySearchHit>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "uri") is not { } uri || !uri.StartsWith("spotify:track:", StringComparison.Ordinal)) continue;
                if (Text(item, "name") is not { Length: > 0 } name) continue;
                var artists = item.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                    ? ar.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => Text(a, "name")).Where(n => !string.IsNullOrEmpty(n))
                    : [];
                var ms = item.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetDouble()
                    : item.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
                var isrc = item.TryGetProperty("external_ids", out var ids) && ids.ValueKind == JsonValueKind.Object ? Text(ids, "isrc") : null;
                hits.Add(new SpotifySearchHit(uri, new JamTrackDto(name, string.Join(", ", artists), null, ms / 1000.0, isrc)));
            }
            return hits;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Un résultat pathfinder : <c>{"item":{"data":{"uri","name","duration":{"totalMilliseconds"},"artists":{"items":[{"profile":{"name"}}]}}}}</c>.</summary>
    private static SpotifySearchHit? PathfinderHit(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        if (Text(d, "uri") is not { } uri || !uri.StartsWith("spotify:track:", StringComparison.Ordinal) || Text(d, "name") is not { Length: > 0 } name) return null;
        var ms = d.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Object
            && du.TryGetProperty("totalMilliseconds", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetDouble() : 0;
        var artists = d.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Object
            && ar.TryGetProperty("items", out var ai) && ai.ValueKind == JsonValueKind.Array
            ? ai.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object)
                .Select(a => Text(a.GetProperty("profile"), "name")).Where(n => !string.IsNullOrEmpty(n))
            : [];
        return new SpotifySearchHit(uri, new JamTrackDto(name, string.Join(", ", artists), null, ms / 1000.0, null));
    }

    /// <summary>
    /// Commande du lecteur de l'hôte, au format connect-state (<c>{"endpoint":"pause"}</c>…). Null si la commande est
    /// incomplète (un saut sans position).
    /// </summary>
    public static JsonObject? ControlCommand(JamControl action, double? positionSeconds)
    {
        JsonObject command = action switch
        {
            JamControl.Pause => new() { ["endpoint"] = "pause" },
            JamControl.Resume => new() { ["endpoint"] = "resume" },
            JamControl.Next => new() { ["endpoint"] = "skip_next" },
            JamControl.Previous => new() { ["endpoint"] = "skip_prev" },
            // « value » pour les anciens lecteurs, « position » + « relative » pour les récents.
            JamControl.Seek when positionSeconds is { } seconds => new()
            {
                ["endpoint"] = "seek_to", ["value"] = Ms(seconds), ["position"] = Ms(seconds), ["relative"] = "beginning",
            },
            _ => null!,
        };
        return command is null ? null : Logged(command);
        static long Ms(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 1000);
    }

    /// <summary>Ajoute un titre à la file de l'hôte (dans un Jam : la file commune).</summary>
    public static JsonObject QueueCommand(string trackUri) => Logged(new JsonObject
    {
        ["endpoint"] = "add_to_queue",
        ["track"] = new JsonObject
        {
            ["uri"] = trackUri,
            ["metadata"] = new JsonObject { ["is_queued"] = "true" },
            ["provider"] = "queue",
        },
    });

    /// <summary>
    /// Lance un titre tout de suite chez l'hôte (contexte = ce titre) ; sa file est conservée. Préféré à « ajouter puis
    /// suivant », qui jouerait le plus ancien titre en file.
    /// </summary>
    public static JsonObject PlayCommand(string trackUri) => Logged(new JsonObject
    {
        ["endpoint"] = "play",
        ["context"] = new JsonObject { ["uri"] = trackUri, ["url"] = $"context://{trackUri}", ["metadata"] = new JsonObject() },
        ["options"] = new JsonObject { ["skip_to"] = new JsonObject { ["track_uri"] = trackUri } },
    });

    private static JsonObject Logged(JsonObject command)
    {
        command["logging_params"] = new JsonObject { ["command_id"] = Guid.NewGuid().ToString("N") };
        return command;
    }

    /// <summary>
    /// Envoie une commande de notre appareil à celui de l'hôte (<c>connect-state/v1/player/command/from/…/to/…</c>).
    /// Rend le code HTTP : 200 accepté ; 403 si le Jam ne nous laisse pas piloter.
    /// </summary>
    public async Task<int> CommandAsync(SpotifyHosts hosts, string accessToken, string fromDevice, string toDevice, JsonObject command, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{hosts.SpClient}/connect-state/v1/player/command/from/{Uri.EscapeDataString(fromDevice)}/to/{Uri.EscapeDataString(toDevice)}")
        {
            Content = new StringContent(new JsonObject { ["command"] = command }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        var status = (int)res.StatusCode;
        if (res.IsSuccessStatusCode) log.LogInformation("Jam : commande {Endpoint} envoyée ({Status})", command["endpoint"], status);
        else log.LogWarning("Jam : commande {Endpoint} refusée ({Status}) : {Body}", command["endpoint"], status,
            (await res.Content.ReadAsStringAsync(ct)) is { Length: > 300 } b ? b[..300] : await res.Content.ReadAsStringAsync(ct));
        return status;
    }

    /// <summary>
    /// Enregistre un appareil caché et non lecteur (connect-state) avec l'identifiant de connexion du dealer, et renvoie
    /// l'état de lecture courant du compte — c'est-à-dire, une fois le Jam rejoint, celui de l'hôte.
    /// </summary>
    public async Task<ClusterState?> RegisterReceiverAsync(SpotifyHosts hosts, string accessToken, string deviceId, string connectionId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            member_type = "CONNECT_STATE",
            device = new { device_info = new { capabilities = new { can_be_player = false, hidden = true, needs_full_player_state = true } } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{hosts.SpClient}/connect-state/v1/devices/hobs_{deviceId}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("X-Spotify-Connection-Id", connectionId);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Enregistrement connect-state refusé : {Status}", (int)res.StatusCode);
            return null;
        }
        return ClusterState.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    /// <summary>Chaîne JSON, ou null si absente ou d'un autre type : un champ qui change de forme ne casse pas le suivi.</summary>
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static HttpRequestMessage Get(string url, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }
}

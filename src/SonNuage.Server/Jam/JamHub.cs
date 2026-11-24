using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SonNuage.Contracts;

namespace SonNuage.Server.Jam;

/// <summary>
/// Chef d'orchestre des Jams suivis : un <see cref="JamFollower"/> par compte au plus, et les flux Server-Sent Events
/// des appareils abonnés. Quand un compte suit un Jam, l'état est poussé à tous ses appareils ; en mémoire, un
/// redémarrage du serveur arrête les suivis (l'appli les relancera).
/// </summary>
public sealed class JamHub(IServiceScopeFactory scopes, ILoggerFactory logs) : IAsyncDisposable
{
    /// <summary>Même forme que ce que lit l'appli (web defaults, énumérations en nombres) : surtout pas le convertisseur chaîne des endpoints.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger _log = logs.CreateLogger<JamHub>();
    private readonly ConcurrentDictionary<Guid, JamFollower> _followers = new();
    private readonly ConcurrentDictionary<Guid, JamStateDto> _states = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<string>>> _subscribers = new();

    /// <summary>
    /// Un seul verrou pour l'état diffusé : qui est le suivi du compte, le calcul de son état, l'envoi aux appareils et
    /// l'abonnement. Les préparations d'un suivi publient depuis plusieurs fils : sans lui, un état « actif » pouvait
    /// arriver après l'état inactif (suivi arrêté entre la vérification et l'envoi), ou un état plus ancien après un
    /// plus récent. Tout ce qu'il protège est bref et sans attente.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>Attente maximale de la réponse de Spotify au premier « join », pour dire tout de suite si c'est refusé.</summary>
    private static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// (Re)commence à suivre le Jam désigné par un jeton de partage, dans le mode d'écoute choisi. Rend faux si Spotify
    /// refuse de nous y faire entrer (Jam fini, compte exclu…) : rien n'est alors suivi.
    /// </summary>
    /// <param name="mayImport">Le compte peut demander des téléchargements : les titres absents peuvent être importés sur
    /// le serveur (sinon l'appareil les joue depuis YouTube en direct).</param>
    public async Task<bool> JoinAsync(Guid userId, string joinToken, JamAudio audio, bool mayImport, CancellationToken ct)
    {
        var gate = Gate(userId);
        await gate.WaitAsync(ct);
        try
        {
            return await JoinLockedAsync(userId, joinToken, audio, mayImport, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> JoinLockedAsync(Guid userId, string joinToken, JamAudio audio, bool mayImport, CancellationToken ct)
    {
        // L'ancien suivi est retiré sans publier d'état inactif : les appareils le prendraient pour la fin du Jam et
        // cesseraient de suivre, alors que le nouveau va prendre la suite. Son dernier état reste ce que reçoit un
        // appareil qui s'abonne pendant son arrêt (jusqu'à ~5 s pour quitter la session) : le nouveau le remplace dès
        // qu'il est enregistré. Il n'est pas créé avant : l'ancien, en quittant la session, l'en ferait sortir.
        if (Detach(userId, publishInactive: false, keepState: true) is { } previous)
        {
            await previous.DisposeAsync();
            if (Replacing is { } replacing) await replacing(userId, previous);
        }
        JamFollower? follower = null;
        follower = new JamFollower(userId, joinToken, audio, mayImport, scopes, build => PublishFrom(follower, userId, build), Ended, _log);
        Register(userId, follower);
        bool joined;
        try
        {
            joined = await follower.Joined.WaitAsync(JoinWait, ct);
        }
        catch (TimeoutException)
        {
            // Spotify lent : on laisse le suivi continuer, il finira seul si le « join » échoue.
            return true;
        }
        if (!joined)
        {
            // Refusé : plus rien n'est suivi, les appareils doivent le savoir (le suivi a pu s'arrêter de lui-même avant
            // d'être enregistré, sans rien publier). Seulement s'il est encore le suivi du compte.
            if (Detach(userId, publishInactive: true, only: follower) is { } refused) await refused.DisposeAsync();
            else PublishInactiveIfIdle(userId);
        }
        return joined;
    }

    /// <summary>Le suivi devient celui du compte (internal pour les tests).</summary>
    internal void Register(Guid userId, JamFollower follower)
    {
        lock (_lock) _followers[userId] = follower;
    }

    /// <summary>Pour les tests : appelé quand l'ancien suivi est arrêté, juste avant d'enregistrer le nouveau.</summary>
    internal Func<Guid, JamFollower, Task>? Replacing { get; set; }

    /// <summary>
    /// Le suivi s'est arrêté de lui-même (Jam fini, exclu, connexion perdue pour de bon) : les appareils l'apprennent.
    /// Seulement s'il est toujours le suivi du compte (un nouveau « join » a pu le remplacer entre-temps).
    /// </summary>
    private void Ended(JamFollower follower)
    {
        if (Detach(follower.UserId, publishInactive: true, only: follower) is null) return;
        _log.LogInformation("Jam : fin du suivi pour le compte {User}", follower.UserId);
        // Pas d'attente ici : on est dans la boucle du suivi, qui se termine juste après.
        _ = follower.DisposeAsync().AsTask();
    }

    /// <summary>Commande au lecteur de l'hôte du Jam suivi par ce compte ; NotReady s'il n'en suit aucun.</summary>
    public Task<JamCommandResult> SendAsync(Guid userId, System.Text.Json.Nodes.JsonObject command, CancellationToken ct) =>
        _followers.TryGetValue(userId, out var follower) ? follower.SendAsync(command, ct) : Task.FromResult(JamCommandResult.NotReady);

    public Task<JamCommandResult> QueueAsync(Guid userId, JamTrackDto track, bool playNow, CancellationToken ct) =>
        _followers.TryGetValue(userId, out var follower) ? follower.QueueAsync(track, playNow, ct) : Task.FromResult(JamCommandResult.NotReady);

    /// <summary>Arrête de suivre : l'état redevient inactif pour tous les appareils du compte.</summary>
    public async Task LeaveAsync(Guid userId)
    {
        var gate = Gate(userId);
        await gate.WaitAsync();
        try
        {
            if (Detach(userId, publishInactive: true) is { } follower) await follower.DisposeAsync();
            else PublishInactiveIfIdle(userId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Retire le suivi du compte (s'il est <paramref name="only"/>, quand il est donné) et oublie son dernier état (sauf
    /// <paramref name="keepState"/>) ; avec <paramref name="publishInactive"/>, les appareils reçoivent l'état inactif
    /// dans le même verrou : aucun état publié ensuite par ce suivi ne passe plus (il n'est plus celui du compte). Rend
    /// le suivi retiré, à arrêter.
    /// </summary>
    private JamFollower? Detach(Guid userId, bool publishInactive, JamFollower? only = null, bool keepState = false)
    {
        lock (_lock)
        {
            JamFollower? removed;
            if (only is null) { if (!_followers.TryRemove(userId, out removed)) removed = null; }
            else removed = _followers.TryRemove(new KeyValuePair<Guid, JamFollower>(userId, only)) ? only : null;
            if (removed is null) return null;
            if (!keepState) _states.TryRemove(userId, out _);
            if (publishInactive) Publish(userId, JamStateDto.Inactive);
            return removed;
        }
    }

    /// <summary>Aucun suivi pour ce compte : l'état inactif est poussé (et un dernier état périmé oublié).</summary>
    private void PublishInactiveIfIdle(Guid userId)
    {
        lock (_lock)
        {
            if (_followers.ContainsKey(userId)) return;
            Publish(userId, JamStateDto.Inactive);
            _states.TryRemove(userId, out _);
        }
    }

    /// <summary>Un seul « join » ou « leave » à la fois par compte : deux appareils qui rejoignent ensemble ne doivent
    /// pas laisser un suivi orphelin (toujours connecté à Spotify, jamais arrêté). Une entrée par compte qui a suivi un
    /// Jam depuis le démarrage, jamais retirée : la retirer sans risque demanderait de savoir que personne n'attend.</summary>
    private SemaphoreSlim Gate(Guid userId) => _gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <summary>État du compte : celui de son suivi, sinon le dernier publié (ancien suivi en cours de remplacement).</summary>
    public JamStateDto Current(Guid userId)
    {
        lock (_lock) return _followers.TryGetValue(userId, out var f) ? f.Current() : _states.GetValueOrDefault(userId, JamStateDto.Inactive);
    }

    /// <summary>Abonne un appareil au flux d'état du compte. L'état courant est livré tout de suite.</summary>
    public (ChannelReader<string> Reader, IDisposable Lease) Subscribe(Guid userId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        lock (_lock)
        {
            _subscribers.GetOrAdd(userId, _ => new())[id] = channel;
            // État recalculé maintenant (pas le dernier publié) : son AsOfUnixMs sert à l'appareil d'horloge de référence.
            // Dans le verrou : aucune publication ne peut s'intercaler et arriver avant cet état plus ancien.
            channel.Writer.TryWrite(Serialize(Current(userId)));
        }
        return (channel.Reader, new Lease(() =>
        {
            lock (_lock)
            {
                // Dernier appareil parti : l'entrée du compte est retirée (tout ajout passe aussi par ce verrou).
                if (_subscribers.TryGetValue(userId, out var d) && d.TryRemove(id, out _) && d.IsEmpty) _subscribers.TryRemove(userId, out _);
            }
            channel.Writer.TryComplete();
        }));
    }

    /// <summary>
    /// État publié par un suivi : seulement s'il est toujours celui du compte. Ses préparations en cours peuvent finir
    /// après son arrêt (ou son remplacement par un nouveau « join ») et ne doivent pas ranimer un Jam terminé. L'état est
    /// calculé dans le verrou : deux publications concurrentes partent dans l'ordre de leur calcul.
    /// </summary>
    private void PublishFrom(JamFollower? follower, Guid userId, Func<JamStateDto> build)
    {
        lock (_lock)
        {
            if (follower is not null && _followers.TryGetValue(userId, out var current) && ReferenceEquals(current, follower)) Publish(userId, build());
        }
    }

    /// <summary>Enregistre et pousse un état ; appelé dans <see cref="_lock"/> (internal pour les tests).</summary>
    internal void Publish(Guid userId, JamStateDto state)
    {
        lock (_lock)
        {
            _states[userId] = state;
            if (!_subscribers.TryGetValue(userId, out var devices)) return;
            var json = Serialize(state);
            foreach (var channel in devices.Values) channel.Writer.TryWrite(json);
        }
    }

    /// <summary>
    /// Sérialise un état comme le lit l'appli (énumérations en nombres, web defaults). À utiliser aussi pour les
    /// réponses one-shot (<c>/join</c>, <c>/state</c>) afin qu'elles correspondent au flux et non au convertisseur
    /// chaîne global des endpoints.
    /// </summary>
    public static string Serialize(JamStateDto state) => JsonSerializer.Serialize(state, Json);

    public async ValueTask DisposeAsync()
    {
        foreach (var userId in _followers.Keys)
            if (Detach(userId, publishInactive: false) is { } follower) await follower.DisposeAsync();
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Jam;

/// <summary>Droit d'un compte à faire importer les titres d'un Jam sur le serveur.</summary>
public enum JamImportRight
{
    Allowed,
    /// <summary>Ni admin ni contributeur accepté d'office : l'appareil joue YouTube en direct.</summary>
    NotAllowed,
    /// <summary>Quota de demandes des dernières 24 h atteint.</summary>
    Quota,
}

/// <summary>Issue d'une résolution : où on en est, et la piste si elle est jouable.</summary>
public sealed record JamResolution(JamResolve State, TrackDto? Track, string? VideoId = null);

/// <summary>
/// Retrouve le titre d'un Jam dans la bibliothèque, sinon lance (si autorisé) son import depuis YouTube et renvoie
/// l'avancement. Chaque candidat passe par <see cref="JamMatch.Evaluate"/> (durée, version, artiste, titre, ISRC) et
/// chaque décision est journalisée avec sa raison.
/// </summary>
public sealed class JamResolver(YtDlp ytDlp, YoutubeImporter importer, Microsoft.Extensions.Options.IOptions<SonNuageOptions> options, ILogger<JamResolver> log)
{
    /// <summary>Cherche dans la bibliothèque. Trouvé : la piste ; sinon null.</summary>
    public async Task<TrackDto?> FindInLibraryAsync(SonNuageDbContext db, Guid userId, JamTrackDto track, CancellationToken ct)
    {
        if (LibraryPrefixes(track.Title) is not [var full, ..] prefixes) return null;
        // SearchKey commence par la clé du titre (voir LibraryIndexer) : un titre identique se filtre en base, au lieu de
        // trier en mémoire 200 pistes prises au hasard parmi celles qui contiennent le mot (« Home », « Intro »…).
        var bare = prefixes[^1];
        var candidates = await db.Tracks.AsNoTracking()
            .Where(t => t.SearchKey.StartsWith(full) || t.SearchKey.StartsWith(bare)).Take(500).ToListAsync(ct);
        // Origine des fichiers inconnue (souvent un clip importé) : tolérance de durée d'une vidéo.
        var best = JamMatch.Best(track, candidates, t => new JamCandidate(t.Title, t.ArtistName, t.DurationSeconds, null, JamSourceKind.Video));
        Trace(track, "bibliothèque", candidates.Count, best is { } b ? $"« {b.Item.Title} » — {b.Item.ArtistName}" : null, best?.Verdict);
        var match = best?.Item;
        if (match is null) return null;
        var dto = await LibraryEndpoints.TracksInOrderAsync(db, userId, [match.Id], ct);
        return dto.FirstOrDefault();
    }

    /// <summary>
    /// Débuts de SearchKey à chercher : la clé du titre entier, et celle du nom seul (sans « - Remastered 2009 » ni
    /// « feat. … », que la clé ne retire pas). <see cref="JamMatch"/> juge ensuite la version. Vide si le titre n'a pas
    /// de clé.
    /// </summary>
    public static IReadOnlyList<string> LibraryPrefixes(string title)
    {
        var full = NameNormalizer.Key(title);
        if (full.Length == 0) return [];
        var bare = NameNormalizer.Key(JamMatch.CoreTitle(title));
        return bare.Length == 0 || bare == full ? [full + " "] : [full + " ", bare + " "];
    }

    /// <summary>État d'un import déjà lancé pour cette vidéo : terminé (piste), en cours, ou échoué.</summary>
    public async Task<JamResolution> CheckImportAsync(SonNuageDbContext db, Guid userId, string videoId, CancellationToken ct)
    {
        var item = await db.YoutubeItems.AsNoTracking().FirstOrDefaultAsync(i => i.VideoId == videoId, ct);
        if (item is null) return new JamResolution(JamResolve.Unavailable, null);
        if (item.Status == YoutubeItemStatus.Failed || item.Status == YoutubeItemStatus.Skipped) return new JamResolution(JamResolve.Unavailable, null);
        if (item.Status == YoutubeItemStatus.Done && item.TrackId is { } id)
        {
            var dto = await LibraryEndpoints.TracksInOrderAsync(db, userId, [id], ct);
            return dto.Count > 0 ? new JamResolution(JamResolve.Matched, dto[0], videoId) : new JamResolution(JamResolve.Importing, null, videoId);
        }
        return new JamResolution(JamResolve.Importing, null, videoId);
    }

    /// <summary>
    /// Lance l'import YouTube du titre (meilleur résultat sûr) pour le compte, s'il en a le droit (voir
    /// <see cref="ImportRightAsync"/>). Renvoie la vidéo à suivre (et si un import nouveau est parti : une vidéo déjà
    /// importée ou en cours n'en lance pas), ou null si l'import est refusé ou sans résultat convaincant. Le suivi
    /// d'avancement se fait ensuite via <see cref="CheckImportAsync"/>.
    /// </summary>
    public async Task<(string VideoId, bool Queued)?> StartImportAsync(SonNuageDbContext db, Guid userId, JamTrackDto track, CancellationToken ct)
    {
        try
        {
            // Avant toute recherche : un compte sans droit ne fait même pas tourner yt-dlp.
            var right = await ImportRightAsync(db, userId, DateTimeOffset.UtcNow, ct);
            if (right != JamImportRight.Allowed)
            {
                log.LogInformation("Jam : « {Title} » — {Artist} : import refusé au compte ({Right})", track.Title, track.Artist, right);
                return null;
            }
            var query = new MatchQuery(track.Artist, track.Title, track.DurationSeconds > 0 ? track.DurationSeconds : null);
            var ranked = await ytDlp.MatchAsync(query, options.Value.MatchOfficialMinScore, ct);
            var best = PickImport(ranked, track);
            Trace(track, "import YouTube", ranked.Count, best is { } p ? $"« {p.Entry.Title} » — {p.Entry.Channel}" : null, null);
            if (best is null) return null;
            var video = ImportVideo(best.Entry, track);
            var queued = false;
            right = await ImportWithinQuotaAsync(db, userId, video,
                async () => queued = (await importer.EnqueueAsync(db, [video], albumHint: null, subscriptionId: null, ct)).Queued > 0,
                DateTimeOffset.UtcNow, ct);
            if (right != JamImportRight.Allowed)
            {
                log.LogInformation("Jam : « {Title} » — {Artist} : import refusé au compte ({Right})", track.Title, track.Artist, right);
                return null;
            }
            return (best.Entry.Id, queued);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Import YouTube du titre de Jam « {Title} » impossible", track.Title);
            return null;
        }
    }

    /// <summary>
    /// Un import lancé par un Jam est une demande de téléchargement comme une autre : admin, ou contributeur dont les
    /// demandes sont acceptées d'office (sinon l'import passerait outre l'approbation), dans son quota sur 24 h.
    /// </summary>
    internal static async Task<JamImportRight> ImportRightAsync(SonNuageDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Role, u.RequestsPerDay, u.AutoApproveRequests }).SingleOrDefaultAsync(ct);
        if (user is null) return JamImportRight.NotAllowed;
        if (user.Role == Roles.Admin) return JamImportRight.Allowed;
        if (user.Role != Roles.Contributor || !user.AutoApproveRequests) return JamImportRight.NotAllowed;
        return await DownloadRequestEndpoints.UsedAsync(db, userId, now, ct) >= user.RequestsPerDay ? JamImportRight.Quota : JamImportRight.Allowed;
    }

    /// <summary>
    /// Revérifie le droit, met la vidéo en file (<paramref name="enqueue"/> : vrai si un import nouveau est parti) et le
    /// compte, d'un seul tenant par compte : le titre courant et les suivants se préparent en parallèle, et chacun aurait
    /// vu le quota libre.
    /// </summary>
    internal static async Task<JamImportRight> ImportWithinQuotaAsync(SonNuageDbContext db, Guid userId, YoutubeVideoDto video, Func<Task<bool>> enqueue, DateTimeOffset now, CancellationToken ct)
    {
        var gate = ImportGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var right = await ImportRightAsync(db, userId, now, ct);
            if (right != JamImportRight.Allowed) return right;
            // Vidéo déjà importée ou en cours : rien de nouveau, rien à compter.
            if (await enqueue()) await RecordImportAsync(db, userId, video, now, ct);
            return JamImportRight.Allowed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Un verrou par compte (quelques comptes au plus : jamais libérés).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> ImportGates = new();

    /// <summary>
    /// Trace l'import comme une demande acceptée d'office : elle compte dans le quota du jour (même décompte que les
    /// demandes) et l'admin la voit dans l'historique.
    /// </summary>
    internal static async Task RecordImportAsync(SonNuageDbContext db, Guid userId, YoutubeVideoDto video, DateTimeOffset now, CancellationToken ct)
    {
        db.DownloadRequests.Add(new DownloadRequest
        {
            UserId = userId,
            VideoId = video.VideoId,
            Title = video.Title.Length <= 500 ? video.Title : video.Title[..500],
            Channel = video.Channel is { Length: > 200 } c ? c[..200] : video.Channel,
            DurationSeconds = video.DurationSeconds is > 0 and < 86_400 ? video.DurationSeconds : null,
            Note = "Jam Spotify",
            Decision = DownloadRequestDecision.Accepted,
            AutoApproved = true,
            CreatedAt = now,
            DecidedAt = now,
            YoutubeItem = await db.YoutubeItems.FirstOrDefaultAsync(i => i.VideoId == video.VideoId, ct),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Le candidat à importer : le mieux noté par <see cref="JamMatch"/> (le classement de la recherche départage) ;
    /// null si aucun n'est sûr. Mieux vaut ne rien importer qu'importer un autre morceau.
    /// </summary>
    public static RankedEntry? PickImport(IReadOnlyList<RankedEntry> ranked, JamTrackDto track) =>
        JamMatch.Best(track, ranked.OrderByDescending(r => r.Score),
            r => new JamCandidate(r.Entry.Title, r.Entry.Channel ?? "", r.Entry.DurationSeconds ?? 0, null, JamSourceKind.Video))?.Item;

    /// <summary>
    /// La vidéo à importer, nommée d'après le titre de l'hôte (Spotify) : le titre d'une vidéo est souvent à l'envers
    /// (« Titre - Artiste ») ou habillé, et l'import l'aurait mal découpé. « Artiste - Titre » est la forme que
    /// <see cref="TitleParser"/> lit sans ambiguïté ; la chaîne reste la vraie.
    /// </summary>
    public static YoutubeVideoDto ImportVideo(YtEntry e, JamTrackDto track) =>
        new(e.Id, $"{track.Artist} - {track.Title}", e.Channel, e.DurationSeconds, e.Thumbnail, null, e.Album);

    /// <summary>Une ligne par décision : quel titre, où, combien de candidats, lequel et pourquoi.</summary>
    private void Trace(JamTrackDto track, string where, int count, string? chosen, JamVerdict? verdict) =>
        log.LogInformation("Jam : « {Title} » — {Artist} ({Isrc}) : {Where}, {Count} candidat(s) → {Chosen}{Reason}",
            track.Title, track.Artist, track.Isrc ?? "sans ISRC", where, count, chosen ?? "aucun sûr",
            verdict is { } v ? $" ({v.Reason}, {v.Score})" : "");
}

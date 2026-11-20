using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Api;
using SonNuage.Server.Data;
using SonNuage.Server.Library;
using SonNuage.Server.Youtube;

namespace SonNuage.Server.Jam;

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
        var titleKey = NameNormalizer.Key(track.Title);
        if (titleKey.Length == 0) return null;
        // SearchKey commence par la clé du titre (voir LibraryIndexer) : un titre identique se filtre en base, au lieu de
        // trier en mémoire 200 pistes prises au hasard parmi celles qui contiennent le mot (« Home », « Intro »…).
        var prefix = titleKey + " ";
        var candidates = await db.Tracks.AsNoTracking().Where(t => t.SearchKey.StartsWith(prefix)).Take(500).ToListAsync(ct);
        // Origine des fichiers inconnue (souvent un clip importé) : tolérance de durée d'une vidéo.
        var best = JamMatch.Best(track, candidates, t => new JamCandidate(t.Title, t.ArtistName, t.DurationSeconds, null, JamSourceKind.Video));
        Trace(track, "bibliothèque", candidates.Count, best is { } b ? $"« {b.Item.Title} » — {b.Item.ArtistName}" : null, best?.Verdict);
        var match = best?.Item;
        if (match is null) return null;
        var dto = await LibraryEndpoints.TracksInOrderAsync(db, userId, [match.Id], ct);
        return dto.FirstOrDefault();
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
    /// Lance l'import YouTube du titre (meilleur résultat sûr). Renvoie l'identifiant de la vidéo mise en file, ou null
    /// si aucun résultat convaincant. Le suivi d'avancement se fait ensuite via <see cref="CheckImportAsync"/>.
    /// </summary>
    public async Task<string?> StartImportAsync(SonNuageDbContext db, JamTrackDto track, CancellationToken ct)
    {
        try
        {
            var query = new MatchQuery(track.Artist, track.Title, track.DurationSeconds > 0 ? track.DurationSeconds : null);
            var ranked = await ytDlp.MatchAsync(query, options.Value.MatchOfficialMinScore, ct);
            var best = PickImport(ranked, track);
            Trace(track, "import YouTube", ranked.Count, best is { } p ? $"« {p.Entry.Title} » — {p.Entry.Channel}" : null, null);
            if (best is null) return null;
            await importer.EnqueueAsync(db, [ImportVideo(best.Entry, track)], albumHint: null, subscriptionId: null, ct);
            return best.Entry.Id;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Import YouTube du titre de Jam « {Title} » impossible", track.Title);
            return null;
        }
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

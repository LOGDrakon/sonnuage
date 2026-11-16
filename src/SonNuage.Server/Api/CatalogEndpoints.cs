using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;
using SonNuage.Server.Library;

namespace SonNuage.Server.Api;

/// <summary>
/// Recherche dans le catalogue de Deezer pour la page Demandes : on choisit un titre, un album ou un artiste, et la
/// vidéo YouTube est trouvée ensuite (recherche par correspondance, demande d'album). Seul le texte tapé est envoyé.
/// </summary>
public static class CatalogEndpoints
{
    private const int Found = 10;

    public static void MapCatalogEndpoints(this RouteGroupBuilder requests)
    {
        requests.MapGet("/catalog", async (string? q, DeezerClient deezer, SonNuageDbContext db, CancellationToken ct) =>
        {
            q = q?.Trim();
            if (string.IsNullOrEmpty(q) || q.Length > 200) return Results.BadRequest();
            var found = await deezer.SearchAsync(q, Found, ct);
            var library = await LibraryAsync(db, found.Tracks.Select(t => t.Artist).Concat(found.Albums.Select(a => a.Artist)).Concat(found.Artists.Select(a => a.Name)), ct);
            return Results.Ok(new CatalogSearchDto(
                found.Tracks.Select(t => t with { InLibrary = library.HasTrack(t.Artist, t.Title) }).ToList(),
                found.Albums.Select(a => a with { InLibrary = library.HasAlbum(a.Artist, a.Title) }).ToList(),
                found.Artists.Select(a => a with { InLibrary = library.HasArtist(a.Name) }).ToList()));
        });

        requests.MapGet("/catalog/artists/{id:long}/albums", async (long id, string? name, DeezerClient deezer, SonNuageDbContext db, CancellationToken ct) =>
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) return Results.BadRequest();
            var albums = await deezer.ArtistAlbumsAsync(id, name, ct);
            var library = await LibraryAsync(db, [name], ct);
            return Results.Ok(albums.Select(a => a with { InLibrary = library.HasAlbum(a.Artist, a.Title) }).ToList());
        });
    }

    /// <summary>Ce que la bibliothèque a de ces artistes : leurs albums et leurs titres, en clés normalisées.</summary>
    private static async Task<Presence> LibraryAsync(SonNuageDbContext db, IEnumerable<string> artistNames, CancellationToken ct)
    {
        var keys = artistNames.Select(NameNormalizer.Key).ToHashSet();
        var artists = await db.Artists.Where(a => keys.Contains(a.NormalizedName)).Select(a => new { a.Id, a.NormalizedName }).ToListAsync(ct);
        var ids = artists.Select(a => a.Id).ToList();
        var albums = await db.Albums.Where(a => ids.Contains(a.ArtistId)).Select(a => new { a.ArtistId, a.NormalizedTitle }).ToListAsync(ct);
        var tracks = await db.Tracks.Where(t => ids.Contains(t.ArtistId) || ids.Contains(t.Album.ArtistId))
            .Select(t => new { t.ArtistId, AlbumArtistId = t.Album.ArtistId, t.Title }).ToListAsync(ct);
        var nameOf = artists.ToDictionary(a => a.Id, a => a.NormalizedName);
        return new Presence(
            nameOf.Values.ToHashSet(),
            albums.Select(a => (nameOf[a.ArtistId], a.NormalizedTitle)).ToHashSet(),
            tracks.SelectMany(t => new[] { t.ArtistId, t.AlbumArtistId }.Where(nameOf.ContainsKey)
                .Select(id => (nameOf[id], NameNormalizer.Key(t.Title)))).ToHashSet());
    }

    /// <summary>Clés normalisées : « First Album (Deluxe Edition) » retrouve « First Album », « Song (Radio Edit) » retrouve « Song ».</summary>
    private sealed record Presence(HashSet<string> Artists, HashSet<(string Artist, string Title)> Albums, HashSet<(string Artist, string Title)> Tracks)
    {
        public bool HasArtist(string name) => Artists.Contains(NameNormalizer.Key(name));
        public bool HasAlbum(string artist, string title) => Albums.Contains((NameNormalizer.Key(artist), NameNormalizer.Key(title)));
        public bool HasTrack(string artist, string title) => Tracks.Contains((NameNormalizer.Key(artist), NameNormalizer.Key(title)));
    }
}

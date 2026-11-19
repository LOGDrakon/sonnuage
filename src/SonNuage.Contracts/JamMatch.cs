using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SonNuage.Contracts;

/// <summary>Nature d'une source candidate : sa durée est-elle celle de l'enregistrement, ou peut-elle déborder ?</summary>
public enum JamSourceKind
{
    /// <summary>L'enregistrement seul (titre YouTube Music « Song ») : la durée doit coller.</summary>
    Audio,
    /// <summary>Une vidéo ou un fichier d'origine inconnue : un clip peut ajouter une intro ou une fin.</summary>
    Video,
}

/// <summary>Une source qui pourrait jouer le titre de l'hôte : piste de la bibliothèque, résultat YouTube…</summary>
public sealed record JamCandidate(string Title, string Artist, double DurationSeconds, string? Isrc, JamSourceKind Kind);

/// <summary>Décision sur un candidat, avec sa raison (journalisée : un mauvais choix doit pouvoir s'expliquer).</summary>
public sealed record JamVerdict(bool Accepted, int Score, string Reason)
{
    public static JamVerdict Reject(string reason) => new(false, 0, reason);
}

/// <summary>
/// Le titre rejoué pour un Jam doit être l'enregistrement de l'hôte. Une seule règle, pour la bibliothèque, l'import
/// du serveur et la lecture YouTube directe :
/// <list type="number">
/// <item>la durée colle (toujours vérifiée, même avec un ISRC) ;</item>
/// <item>même ISRC : c'est le même enregistrement ;</item>
/// <item>sinon, même version (pas un remix, un live, une version accélérée… pour l'original) ;</item>
/// <item>l'un des artistes de l'hôte apparaît (artiste ou titre du candidat) ;</item>
/// <item>tous les mots du titre de l'hôte sont dans celui du candidat, et au plus un mot en trop inexpliqué
/// (« Official Video », « Lyrics », l'artiste, une année ne comptent pas).</item>
/// </list>
/// Un doute = un refus : mieux vaut attendre l'import qu'entendre un autre morceau.
/// </summary>
public static partial class JamMatch
{
    /// <summary>Meilleur candidat accepté (score, puis ordre d'origine), avec son verdict ; null si aucun.</summary>
    public static (T Item, JamVerdict Verdict)? Best<T>(JamTrackDto host, IEnumerable<T> items, Func<T, JamCandidate> candidate)
    {
        (T Item, JamVerdict Verdict)? best = null;
        foreach (var item in items)
        {
            var verdict = Evaluate(host, candidate(item));
            if (verdict.Accepted && (best is null || verdict.Score > best.Value.Verdict.Score)) best = (item, verdict);
        }
        return best;
    }

    public static JamVerdict Evaluate(JamTrackDto host, JamCandidate candidate)
    {
        if (!DurationFits(host.DurationSeconds, candidate.DurationSeconds, candidate.Kind)) return JamVerdict.Reject("durée");
        if (host.Isrc is { Length: > 0 } isrc && string.Equals(isrc, candidate.Isrc, StringComparison.OrdinalIgnoreCase))
            return new(true, 200, "isrc");
        if (!SameVersion(host.Title, candidate.Title)) return JamVerdict.Reject("version");

        var titleWords = Words(candidate.Title);
        var artistWords = Words(candidate.Artist);
        var artists = ArtistNames(host.Artist).ToList();
        var inArtistField = artists.Any(a => ArtistIn(a, artistWords, Squash(candidate.Artist)));
        if (!inArtistField && !artists.Any(a => ArtistIn(a, titleWords, Squash(candidate.Title)))) return JamVerdict.Reject("artiste");

        var core = Words(CoreTitle(host.Title));
        if (core.Count == 0) core = Words(host.Title);
        if (!core.IsSubsetOf(titleWords)) return JamVerdict.Reject("titre");
        var explained = new HashSet<string>(core);
        explained.UnionWith(Words(host.Title));
        foreach (var a in artists) explained.UnionWith(Words(a));
        explained.UnionWith(artistWords);
        var extra = titleWords.Count(w => !explained.Contains(w) && !Noise.Contains(w) && !w.All(char.IsDigit));
        if (extra > 1) return JamVerdict.Reject("titre");

        var score = 60;
        if (extra == 0) score += 15;
        if (inArtistField) score += 10;
        if (candidate.Kind == JamSourceKind.Audio) score += 5;
        if (host.DurationSeconds > 0 && candidate.DurationSeconds > 0)
            score += (int)Math.Round(15 * (1 - Math.Abs(host.DurationSeconds - candidate.DurationSeconds) / Tolerance(host.DurationSeconds, candidate.Kind)));
        return new(true, score, "titre, artiste, durée");
    }

    /// <summary>
    /// Mêmes marques de version (remix, live, acoustique, accéléré…) dans les suffixes : entre parenthèses, crochets ou
    /// après un tiret. Les mots du nom lui-même ne comptent pas : « Live and Let Die » n'est pas un live.
    /// </summary>
    public static bool SameVersion(string hostTitle, string candidateTitle) =>
        VersionTags(hostTitle).SetEquals(VersionTags(candidateTitle));

    /// <summary>Durées compatibles : ±max(6 s, 4 %) pour l'audio seul, ±max(15 s, 12 %) pour une vidéo. Inconnue : vrai.</summary>
    public static bool DurationFits(double hostSeconds, double candidateSeconds, JamSourceKind kind) =>
        hostSeconds <= 0 || candidateSeconds <= 0 || Math.Abs(hostSeconds - candidateSeconds) <= Tolerance(hostSeconds, kind);

    private static double Tolerance(double host, JamSourceKind kind) =>
        kind == JamSourceKind.Audio ? Math.Max(6, host * 0.04) : Math.Max(15, host * 0.12);

    /// <summary>Mots d'habillage d'un titre YouTube ou d'un fichier, qui ne changent pas le morceau.</summary>
    private static readonly HashSet<string> Noise =
    [
        "official", "officiel", "officielle", "video", "clip", "audio", "lyric", "lyrics", "paroles", "music", "musique",
        "mv", "hd", "hq", "4k", "visualizer", "visualiser", "feat", "ft", "featuring", "with", "prod", "by", "remastered",
        "remaster", "version", "explicit", "clean", "topic", "vevo", "radio", "edit", "single", "original", "mix", "and",
        "the", "x", "vs", "full", "song", "track", "out", "now", "new",
    ];

    /// <summary>
    /// Le nom entier et chacun des artistes réunis : « Gym Class Heroes, Adam Levine », « ivycomb feat. YonKaGor »,
    /// « A &amp; B », « A x B »… Un seul suffit (« Tyler, The Creator » reste trouvé par son nom entier).
    /// </summary>
    private static IEnumerable<string> ArtistNames(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) yield break;
        yield return artist;
        foreach (var part in ArtistSeparators().Split(artist))
            if (part.Trim() is { Length: > 0 } name) yield return name;
    }

    [GeneratedRegex(@"\s*(?:,|&|\+|/|\b(?:feat|ft)\.?|\bfeaturing\b|\bwith\b|\bx\b|\bvs\.?)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    /// <summary>
    /// L'artiste est-il là : tous ses mots présents, ou son nom collé dans un nom de chaîne (« TheWeekndVEVO ») quand il
    /// est assez long pour ne pas apparaître par hasard.
    /// </summary>
    private static bool ArtistIn(string artist, HashSet<string> words, string squashed)
    {
        var mine = Words(artist);
        mine.ExceptWith(["the", "and"]);
        if (mine.Count == 0) return false;
        if (mine.IsSubsetOf(words)) return true;
        var key = Squash(artist);
        return key.Length >= 5 && squashed.Contains(key, StringComparison.Ordinal);
    }

    /// <summary>Le nom du morceau sans suffixes (parenthèses, crochets, tiret) ni « feat. … ».</summary>
    private static string CoreTitle(string title)
    {
        var core = Brackets().Replace(title, " ");
        core = DashTail().Replace(core, "");
        return Featuring().Replace(core, "").Trim();
    }

    private static HashSet<string> VersionTags(string title)
    {
        var tags = new HashSet<string>();
        foreach (Match m in Suffixes().Matches(title ?? ""))
        {
            var text = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
            foreach (var (tag, pattern) in Tags)
                if (pattern.IsMatch(text)) tags.Add(tag);
        }
        return tags;
    }

    private static readonly (string Tag, Regex Pattern)[] Tags =
    [
        ("remix", Word("remix|rmx|bootleg")),
        ("live", Word("live")),
        ("acoustic", Word("acoustic|acoustique|unplugged")),
        ("instrumental", Word("instrumental|karaoke|off vocal")),
        ("acapella", Word("a ?cappella|acapella")),
        ("spedup", Word("sped up|speed up|spedup")),
        ("nightcore", Word("nightcore")),
        ("slowed", Word("slowed")),
        ("reverb", Word("reverb")),
        ("cover", Word("cover")),
        ("extended", Word("extended")),
        ("demo", Word("demo")),
        ("8d", Word("8d")),
        ("piano", Word("piano")),
        ("orchestral", Word("orchestral|symphonic")),
    ];

    private static Regex Word(string words) =>
        new($@"\b(?:{words})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\(([^)]*)\)|\[([^\]]*)\]|【([^】]*)】|\s[-–—]\s(.*)$")]
    private static partial Regex Suffixes();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|【[^】]*】")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s[-–—]\s.*$")]
    private static partial Regex DashTail();

    [GeneratedRegex(@"\s(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    /// <summary>Mots en minuscules sans accents ; un mot est une suite de lettres ou chiffres.</summary>
    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var c in Plain(text))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    private static string Squash(string? text) => string.Concat(Plain(text).Where(char.IsLetterOrDigit));

    private static string Plain(string? text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

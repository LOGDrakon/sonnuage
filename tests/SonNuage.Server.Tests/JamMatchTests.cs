using SonNuage.Contracts;

namespace SonNuage.Server.Tests;

/// <summary>
/// Le titre rejoué pour un Jam doit être l'enregistrement de l'hôte : même morceau, même artiste, même version, même
/// durée. Une règle unique (<see cref="JamMatch.Evaluate"/>) pour la bibliothèque, l'import et YouTube direct.
/// </summary>
public sealed class JamMatchTests
{
    private static JamTrackDto Host(string title, string artist, double seconds, string? isrc = null) =>
        new(title, artist, null, seconds, isrc);

    private static JamCandidate Audio(string title, string artist, double seconds, string? isrc = null) =>
        new(title, artist, seconds, isrc, JamSourceKind.Audio);

    private static JamCandidate Video(string title, string channel, double seconds) =>
        new(title, channel, seconds, null, JamSourceKind.Video);

    // --- Cas réels rencontrés pendant un Jam ---

    [Fact]
    public void A_remix_with_the_same_name_is_rejected()
    {
        var host = Host("Hide and Seek", "Lizz Robinett", 183);
        var verdict = JamMatch.Evaluate(host, Audio("Hide and Seek (Sare Remix)", "Lizz Robinett & SARE", 143));
        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void Another_artists_song_with_the_same_name_is_rejected()
    {
        var host = Host("Hide and Seek", "Imogen Heap", 272);
        Assert.False(JamMatch.Evaluate(host, Audio("Hide and Seek", "Lizz Robinett", 272)).Accepted);
    }

    [Fact]
    public void A_title_with_unexplained_extra_words_is_rejected()
    {
        // Import mal découpé : « 【Original Animation】Take A Chance - Short Ver ».
        var host = Host("Take A Chance", "Some Artist", 78);
        Assert.False(JamMatch.Evaluate(host, Audio("Short Ver", "【Original Animation】Take A Chance", 78)).Accepted);
    }

    [Fact]
    public void A_lyric_video_of_the_same_recording_is_accepted()
    {
        var host = Host("Stereo Hearts (feat. Adam Levine)", "Gym Class Heroes, Adam Levine", 210.96);
        Assert.True(JamMatch.Evaluate(host, Video("Stereo Hearts | Lyrics", "Gym Class Heroes", 211)).Accepted);
    }

    [Fact]
    public void Isrc_noise_results_are_rejected_and_the_real_one_accepted()
    {
        // Recherche YouTube Music par ISRC : le bon titre d'abord, puis du bruit.
        var host = Host("Blinding Lights", "The Weeknd", 200, "USUG11904206");
        Assert.True(JamMatch.Evaluate(host, Audio("Blinding Lights", "The Weeknd", 201)).Accepted);
        Assert.False(JamMatch.Evaluate(host, Audio("Kaalathukkum Nee Venum", "A.R. Rahman", 294)).Accepted);
        Assert.False(JamMatch.Evaluate(host, Audio("Maasi Maasam", "Ilaiyaraaja", 201)).Accepted);
    }

    // --- Règles ---

    [Fact]
    public void Same_isrc_is_proof_even_with_a_different_title()
    {
        var host = Host("Blinding Lights", "The Weeknd", 200, "USUG11904206");
        var verdict = JamMatch.Evaluate(host, Audio("ブラインディング・ライツ", "ザ・ウィークエンド", 200, "usug11904206"));
        Assert.True(verdict.Accepted);
        Assert.Equal("isrc", verdict.Reason);
    }

    [Fact]
    public void Same_isrc_with_a_wrong_duration_is_still_rejected()
    {
        var host = Host("Blinding Lights", "The Weeknd", 200, "USUG11904206");
        Assert.False(JamMatch.Evaluate(host, Audio("Blinding Lights", "The Weeknd", 260, "USUG11904206")).Accepted);
    }

    [Theory]
    [InlineData("The Weeknd - Blinding Lights (Official Video)", "The Weeknd", 202)]
    [InlineData("Blinding Lights", "The Weeknd - Topic", 200)]
    [InlineData("The Weeknd - Blinding Lights [Official Audio]", "TheWeekndVEVO", 200)]
    [InlineData("Blinding Lights (Remastered 2020)", "The Weeknd", 200)]
    public void Cosmetic_differences_are_accepted(string title, string channel, double seconds) =>
        Assert.True(JamMatch.Evaluate(Host("Blinding Lights", "The Weeknd", 200), Video(title, channel, seconds)).Accepted);

    [Fact]
    public void A_featured_artist_alone_is_enough()
    {
        var host = Host("Stereo Hearts (feat. Adam Levine)", "Gym Class Heroes, Adam Levine", 211);
        Assert.True(JamMatch.Evaluate(host, Audio("Stereo Hearts", "Adam Levine", 211)).Accepted);
    }

    [Theory]
    [InlineData("ivycomb feat. YonKaGor", "ivycomb, YonKaGor")]
    [InlineData("ivycomb & Stephanafro", "ivycomb")]
    [InlineData("Black Gryph0n x Baasik", "Baasik")]
    [InlineData("ivycomb", "ivycomb, YonKaGor")]
    public void Featured_and_joined_artists_are_split(string host, string candidate)
    {
        // Cas réel : « Blue Bird » de la bibliothèque (« ivycomb feat. YonKaGor ») introuvable sur Spotify.
        Assert.True(JamMatch.Evaluate(Host("Blue Bird", host, 243), Audio("Blue Bird", candidate, 243)).Accepted);
    }

    [Fact]
    public void An_artist_name_with_a_comma_still_matches()
    {
        var host = Host("EARFQUAKE", "Tyler, The Creator", 190);
        Assert.True(JamMatch.Evaluate(host, Audio("EARFQUAKE", "Tyler, The Creator", 190)).Accepted);
    }

    [Fact]
    public void A_better_candidate_scores_higher()
    {
        var host = Host("Blinding Lights", "The Weeknd", 200);
        var exact = JamMatch.Evaluate(host, Audio("Blinding Lights", "The Weeknd", 200));
        var clip = JamMatch.Evaluate(host, Video("The Weeknd - Blinding Lights (Official Video)", "The Weeknd", 215));
        Assert.True(exact.Accepted && clip.Accepted);
        Assert.True(exact.Score > clip.Score);
    }

    [Theory]
    [InlineData("Hide and Seek", "Hide and Seek (Sare Remix)")]
    [InlineData("Hide and Seek", "Hide and Seek - Sped Up")]
    [InlineData("Hide and Seek", "Hide and Seek [Nightcore]")]
    [InlineData("Creep", "Creep (Acoustic)")]
    [InlineData("Creep", "Creep - Live at Glastonbury")]
    [InlineData("Creep", "Creep (Instrumental)")]
    [InlineData("Creep (Live)", "Creep")]
    [InlineData("Sweater Weather", "Sweater Weather (Slowed + Reverb)")]
    public void A_different_version_is_not_the_same_track(string host, string candidate) =>
        Assert.False(JamMatch.SameVersion(host, candidate));

    [Theory]
    [InlineData("Hide and Seek", "Hide and Seek")]
    [InlineData("Stereo Hearts (feat. Adam Levine)", "Stereo Hearts | Lyrics")]
    [InlineData("Bohemian Rhapsody - Remastered 2011", "Bohemian Rhapsody (Official Video)")]
    [InlineData("Creep - Live", "Creep (Live at Glastonbury)")]
    [InlineData("Song (Sare Remix)", "Song - Sare Remix")]
    [InlineData("Live and Let Die", "Live and Let Die")]
    [InlineData("Live While We're Young", "Live While We're Young (Official Audio)")]
    public void Same_version_with_cosmetic_differences_matches(string host, string candidate) =>
        Assert.True(JamMatch.SameVersion(host, candidate));

    [Theory]
    [InlineData(200, 205, JamSourceKind.Audio, true)]
    [InlineData(200, 209, JamSourceKind.Audio, false)]  // 4 % de 200 = 8 s
    [InlineData(400, 415, JamSourceKind.Audio, true)]
    [InlineData(200, 222, JamSourceKind.Video, true)]   // un clip peut ajouter une intro : 12 %
    [InlineData(200, 260, JamSourceKind.Video, false)]
    [InlineData(0, 143, JamSourceKind.Audio, true)]     // durée inconnue : on ne peut pas exclure
    public void Durations_must_be_close(double host, double candidate, JamSourceKind kind, bool fits) =>
        Assert.Equal(fits, JamMatch.DurationFits(host, candidate, kind));
}

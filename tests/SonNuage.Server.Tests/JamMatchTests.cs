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

    [Theory]
    [InlineData("September", "Earth, Wind & Fire", "Arcade Fire")]
    [InlineData("Dog Days Are Over", "Florence + The Machine", "Machine Head")]
    [InlineData("EARFQUAKE", "Tyler, The Creator", "Creator")]
    public void A_short_piece_of_a_band_name_is_not_that_band(string title, string host, string candidate)
    {
        // « Fire » n'est pas « Earth, Wind & Fire » ; « The Machine » ni « The Creator » ne sont des artistes à part.
        Assert.False(JamMatch.Evaluate(Host(title, host, 215), Audio(title, candidate, 215)).Accepted);
    }

    [Fact]
    public void Joined_artists_are_still_found_after_a_comma_name()
    {
        // Spotify joint les artistes par des virgules : « Tyler, The Creator » reste un nom, l'invité aussi.
        var host = Host("See You Again (feat. Kali Uchis)", "Tyler, The Creator, Kali Uchis", 180);
        Assert.True(JamMatch.Evaluate(host, Audio("See You Again", "Tyler, The Creator", 180)).Accepted);
        Assert.True(JamMatch.Evaluate(host, Audio("See You Again", "Kali Uchis", 180)).Accepted);
    }

    [Theory]
    [InlineData("Metro Boomin, The Weeknd, 21 Savage", "Metro Boomin")]
    [InlineData("Ariana Grande, The Weeknd", "Ariana Grande")]
    [InlineData("Ariana Grande, The Weeknd", "The Weeknd")]
    [InlineData("Future, The Weeknd", "Future")]
    [InlineData("Selena Gomez, A$AP Rocky", "Selena Gomez")]
    [InlineData("Tyler, The Creator, A$AP Rocky", "Tyler, The Creator")]
    [InlineData("Tyler, The Creator", "Tyler, The Creator")]
    [InlineData("Florence + The Machine", "Florence + the Machine")]
    [InlineData("Florence + The Machine", "Florence and the Machine")]
    public void A_second_artist_starting_with_the_is_still_a_separate_artist(string host, string candidate)
    {
        // Spotify joint les artistes par « , » : « The Weeknd » après une virgule n'allonge pas le nom précédent
        // (sauf peut-être « Tyler, The Creator » : les deux lectures sont essayées).
        Assert.True(JamMatch.Evaluate(Host("Song", host, 200), Audio("Song", candidate, 200)).Accepted);
    }

    [Theory]
    [InlineData("Drake, Future", "DrakeVEVO")]
    [InlineData("Eminem, Rihanna", "EminemMusic")]
    public void A_short_artist_is_found_in_its_channel_name(string host, string channel) =>
        Assert.True(JamMatch.Evaluate(Host("Jumpman", host, 200), Video("Jumpman", channel, 200)).Accepted);

    [Theory]
    [InlineData("The Unforgiven II", "Metallica", "Metallica - The Unforgiven II (Official Music Video)")]
    [InlineData("Chapter VI", "Candlemass", "Candlemass - Chapter VI")]
    [InlineData("Part of Me", "Katy Perry", "Katy Perry - Part Of Me (Official)")]
    [InlineData("The Part I Play", "Some Band", "Some Band - The Part I Play")]
    [InlineData("Finding Out True Love Is Blind", "Louis XIV", "Louis XIV - Finding Out True Love Is Blind")]
    public void A_part_number_is_read_after_the_artist_of_a_video_title(string title, string artist, string video) =>
        Assert.True(JamMatch.Evaluate(Host(title, artist, 300), Video(video, artist, 300)).Accepted);

    [Fact]
    public void Another_part_after_the_artist_of_a_video_title_is_rejected()
    {
        var host = Host("The Unforgiven", "Metallica", 380);
        Assert.False(JamMatch.Evaluate(host, Video("Metallica - The Unforgiven III", "Metallica", 380)).Accepted);
    }

    [Theory]
    [InlineData("Radio Ga Ga - Live at Wembley", "Queen - Radio Ga Ga (Live)", "Queen")]
    [InlineData("Radio Ga Ga - Live at Wembley Stadium, July 1986", "Queen - Radio Ga Ga (Live at Wembley)", "Queen")]
    [InlineData("Radio Ga Ga - Live at Wembley Stadium, July 1986", "Radio Ga Ga (Live at Wembley '86)", "Queen")]
    [InlineData("Hotel California - Live; 2013 Remaster", "Hotel California (Live at the Forum)", "Eagles")]
    public void A_live_place_is_read_only_after_live(string host, string candidate, string artist)
    {
        Assert.True(JamMatch.SameVersion(host, candidate));
        Assert.True(JamMatch.Evaluate(Host(host, artist, 300), Video(candidate, artist, 300)).Accepted);
    }

    [Theory]
    [InlineData("Song", "Song, Pt. 2")]
    [InlineData("Song", "Song Part II")]
    [InlineData("Song", "Song II")]
    [InlineData("Song, Pt. 1", "Song, Pt. 2")]
    [InlineData("Song Part 2", "Song")]
    public void Another_part_of_a_song_is_not_the_same_track(string host, string candidate)
    {
        Assert.False(JamMatch.SameVersion(host, candidate));
        Assert.False(JamMatch.Evaluate(Host(host, "Artist", 200), Audio(candidate, "Artist", 200)).Accepted);
    }

    [Theory]
    [InlineData("Song, Pt. 2", "Song Part 2")]
    [InlineData("Song Part II", "Song, Pt. 2")]
    [InlineData("Song II", "Song Pt. 2")]
    public void The_same_part_written_differently_matches(string host, string candidate) =>
        Assert.True(JamMatch.Evaluate(Host(host, "Artist", 200), Audio(candidate, "Artist", 200)).Accepted);

    [Theory]
    [InlineData("Creep (Live at Wembley)", "Creep (Live in Paris)")]
    [InlineData("All Too Well (Taylor's Version)", "All Too Well")]
    [InlineData("All Too Well", "All Too Well (Re-Recorded)")]
    public void Another_concert_or_a_rerecording_is_not_the_same_track(string host, string candidate) =>
        Assert.False(JamMatch.SameVersion(host, candidate));

    [Theory]
    [InlineData("Creep (Live at Wembley)", "Creep - Live at Wembley Stadium")]
    [InlineData("All Too Well (Taylor's Version)", "All Too Well (Taylor’s Version) [Lyrics]")]
    public void The_same_concert_or_rerecording_matches(string host, string candidate) =>
        Assert.True(JamMatch.SameVersion(host, candidate));

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
